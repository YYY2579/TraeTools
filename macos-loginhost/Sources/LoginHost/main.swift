import AppKit
import Foundation
import WebKit

// macOS 登录宿主。契约与 TraeTools.LoginHost（Windows / WinForms + WebView2）逐项对齐：
//   入参  --user-data-dir <dir> --out <file> [--initial-token t] [--expected-uid uid]
//         [--proxy-server addr] [--debug-capture] [--selftest]
//   回写  成功 {"token":"<Cloud-IDE-Token>","session":"<X-Cloudide-Session 或 null>"}
//         失败 {"error":"原因"}
//   退出码 0=已取得 token 2=用户取消/未取得 3=参数错误 4=启动或写回失败
//
// 差异（有意为之，不是漏实现）见文件末尾 KnownDifferences。

// MARK: - 参数

struct Options {
    var userDataDir: String
    var outFile: String
    var initialToken: String?
    var expectedUid: String?
    var proxyServer: String?
    var debugCapture: Bool
    var selfTest: Bool
    /// 仅测试用：给一个 JWT，打印解析出的 data.id 后退出，不建窗口。
    var jwtUidProbe: String?
    /// 仅测试用：用该地址代替 trae.cn 作为起始页，让本地假页面能端到端跑通
    /// 「读 localStorage token → 校验归属 → 抓 session cookie → 回写结果」整条链路。
    var startUrl: String?
}

func parseArgs(_ args: [String]) -> Options? {
    var dir: String?
    var out: String?
    var initial: String?
    var uid: String?
    var proxy: String?
    var jwtProbe: String?
    var startUrl: String?
    var debug = false
    var selftest = false

    var i = 0
    while i < args.count {
        switch args[i] {
        case "--user-data-dir":
            if i + 1 < args.count { dir = args[i + 1]; i += 1 }
        case "--out":
            if i + 1 < args.count { out = args[i + 1]; i += 1 }
        case "--initial-token":
            if i + 1 < args.count { initial = args[i + 1]; i += 1 }
        case "--expected-uid":
            if i + 1 < args.count { uid = args[i + 1]; i += 1 }
        case "--proxy-server":
            if i + 1 < args.count { proxy = args[i + 1]; i += 1 }
        case "--jwt-uid-probe":
            if i + 1 < args.count { jwtProbe = args[i + 1]; i += 1 }
        case "--start-url":
            if i + 1 < args.count { startUrl = args[i + 1]; i += 1 }
        case "--debug-capture": debug = true
        case "--selftest": selftest = true
        default: break
        }
        i += 1
    }

    guard let d = dir, !d.isEmpty, let o = out, !o.isEmpty else { return nil }
    let trimmed = uid?.trimmingCharacters(in: .whitespacesAndNewlines)
    return Options(
        userDataDir: d, outFile: o,
        initialToken: initial,
        expectedUid: (trimmed?.isEmpty == false) ? trimmed : nil,
        proxyServer: proxy,
        debugCapture: debug, selfTest: selftest,
        jwtUidProbe: jwtProbe,
        startUrl: startUrl)
}

// MARK: - Token 判定（对齐 TokenUtils）

/// 与 TokenUtils.ShouldAcceptNewToken 一致：长度不足 40 视为未登录，与初始值相同视为残留。
func shouldAcceptNewToken(_ initial: String?, _ current: String?) -> Bool {
    guard let cur = current, !cur.isEmpty, cur.count >= 40 else { return false }
    return cur != (initial ?? "")
}

/// 从 JWT payload 的 data.id 取平台用户 ID（16 位数字，跨登录恒定）。解析不出返回 nil。
func parseAccountUid(_ jwt: String?) -> String? {
    guard let jwt, !jwt.isEmpty else { return nil }
    let parts = jwt.split(separator: ".")
    guard parts.count >= 2 else { return nil }
    var payload = String(parts[1])
        .replacingOccurrences(of: "-", with: "+")
        .replacingOccurrences(of: "_", with: "/")
    switch payload.count % 4 {
    case 2: payload += "=="
    case 3: payload += "="
    default: break
    }
    guard let data = Data(base64Encoded: payload, options: [.ignoreUnknownCharacters]),
          let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let inner = obj["data"] as? [String: Any],
          let id = inner["id"] as? String, !id.isEmpty
    else { return nil }
    return id
}

// MARK: - 代理
//
// 结论：macOS 上 WKWebView 没有可用的连接级代理入口，本宿主拒绝带 --proxy-server 启动。
// 试过并实测否掉的方案（都跑过本地记录型代理做阳性对照，代理侧零流量）：
//   · UserDefaults 写 ProxySettings —— 裸二进制与包成 .app 各试一次，均不生效
//   · CFPreferencesSetValue(kCFPreferencesCurrentApplication/CurrentUser/AnyHost) —— 同样不生效
// 剩下的路只有私有 API 或改系统全局代理，两者都不该由一个登录窗口静默做。
//
// 影响范围有限：主程序的签到 / 用量 / GitHub 接口 / 飞书走 NetProxy（HttpClientHandler
// + WebProxy），是 .NET 自己的 HTTP 栈，macOS 上照常生效。只有这个内嵌登录浏览器除外。
//
// 为什么是「报错退出」而不是「忽略参数继续」：忽略会让用户以为这个账号走了独立代理，
// 实际和别的账号同一个出口 IP —— 那正是触发 9074 风控的条件，静默降级比直接失败危险。

// MARK: - 回写

func writeJSON(_ payload: [String: Any], to outFile: String) -> Bool {
    let full = (outFile as NSString).standardizingPath
    let parent = (full as NSString).deletingLastPathComponent
    if !parent.isEmpty {
        try? FileManager.default.createDirectory(atPath: parent, withIntermediateDirectories: true)
    }
    guard let data = try? JSONSerialization.data(withJSONObject: payload) else { return false }
    let tmp = full + ".tmp"
    do {
        try data.write(to: URL(fileURLWithPath: tmp), options: .atomic)
        if FileManager.default.fileExists(atPath: full) {
            try FileManager.default.removeItem(atPath: full)
        }
        try FileManager.default.moveItem(atPath: tmp, toPath: full)
        return true
    } catch {
        try? FileManager.default.removeItem(atPath: tmp)
        FileHandle.standardError.write("写回登录结果失败：\(error)\n".data(using: .utf8)!)
        return false
    }
}

func writeResult(_ outFile: String, token: String, session: String?, uidConfirmed: Bool = false) -> Bool {
    writeJSON(["token": token, "session": session ?? NSNull(), "uidConfirmed": uidConfirmed], to: outFile)
}

func writeError(_ outFile: String, _ message: String) {
    _ = writeJSON(["error": message], to: outFile)
}

// MARK: - 登录窗口

final class HostController: NSObject, NSWindowDelegate, WKNavigationDelegate {
    private let opts: Options
    private var window: NSWindow!
    private var webView: WKWebView!
    private var status: NSTextField!
    private var pollTimer: Timer?
    private var finished = false
    private var resultToken: String?
    private var resultSession: String?
    // ---- uid 更换确认：平台会为同一手机号更换 uid（JWT data.id）。不符时不再死拦，
    // 亮出按钮让用户一键确认「这是我的账号」，确认后回写 uidConfirmed=true。----
    private var adoptButton: NSButton!
    private var pendingToken: String?
    private var uidConfirmed = false

    init(_ opts: Options) {
        self.opts = opts
        super.init()
        buildUI()
    }

    private func buildUI() {
        let w = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 480, height: 660),
            styleMask: [.titled, .closable],
            backing: .buffered, defer: false)
        w.title = "登录 Trae 账号"
        w.isReleasedWhenClosed = false
        w.center()
        w.delegate = self

        let content = NSView(frame: w.contentView!.bounds)
        content.autoresizingMask = [.width, .height]

        let cfg = WKWebViewConfiguration()
        // 非持久化：每次登录都是干净会话。这比 Windows 侧的持久化 profile 更严——
        // 上一个账号的残留 Cookie 不可能自动续期出他人 token，跨账号污染在源头就不成立。
        cfg.websiteDataStore = .nonPersistent()
        webView = WKWebView(frame: .zero, configuration: cfg)
        webView.navigationDelegate = self
        webView.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(webView)

        status = NSTextField(labelWithString: "等待登录…")
        status.font = NSFont.systemFont(ofSize: 11)
        status.textColor = .secondaryLabelColor
        status.lineBreakMode = .byWordWrapping
        status.maximumNumberOfLines = 3
        status.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(status)

        // uid 不符时的确认按钮（平时隐藏）。
        adoptButton = NSButton(title: "这是我的账号，采用新 uid 继续", target: self, action: #selector(adoptUidClicked))
        adoptButton.bezelStyle = .rounded
        adoptButton.translatesAutoresizingMaskIntoConstraints = false
        adoptButton.isHidden = true
        content.addSubview(adoptButton)

        NSLayoutConstraint.activate([
            status.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 10),
            status.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -10),
            status.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -8),
            adoptButton.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 10),
            adoptButton.bottomAnchor.constraint(equalTo: status.topAnchor, constant: -6),
            webView.topAnchor.constraint(equalTo: content.topAnchor),
            webView.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            webView.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            webView.bottomAnchor.constraint(equalTo: adoptButton.topAnchor, constant: -6),
        ])
        w.contentView = content
        window = w
    }

    func run() -> Never {
        let target = opts.startUrl ?? "https://www.trae.cn/dashboard"
        guard let url = URL(string: target) else {
            writeError(opts.outFile, "登录地址无效：\(target)")
            exit(4)
        }
        webView.load(URLRequest(url: url))
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)

        pollTimer = Timer(timeInterval: 1.5, repeats: true) { [weak self] _ in
            self?.poll()
        }
        RunLoop.main.add(pollTimer!, forMode: .common)


        // 退出统一走 exit()：NSApplication.terminate 不会再回到这里，
        // 结果文件必须在 finish() 里先写完再退。
        NSApp.run()
        exit(2)
    }

    private func setStatus(_ text: String, _ color: NSColor) {
        DispatchQueue.main.async {
            self.status.stringValue = text
            self.status.textColor = color
        }
    }

    private func poll() {
        guard !finished else { return }
        webView.evaluateJavaScript(
            "(function(){try{var t=localStorage.getItem('Cloud-IDE-Token');return t?t:'';}catch(e){return '';}})()"
        ) { [weak self] value, error in
            guard let self, !self.finished else { return }
            // 页面还没起来 / 正在跳转时脚本报错属正常中间态，继续轮询即可。
            if error != nil { return }
            let token = (value as? String) ?? ""
            guard shouldAcceptNewToken(self.opts.initialToken, token) else { return }
            self.accept(token: token)
        }
    }

    /// 拿到候选 token 后的归属校验 + 会话补读，逻辑与 LoginHostForm 一致。
    private func accept(token: String) {
        if let expected = opts.expectedUid {
            let uid = parseAccountUid(token)
            if uid == nil {
                setStatus("⚠ 无法从 token 解析账号归属（可能官方换了格式），已按原样接受。", .systemYellow)
            } else if uid != expected {
                // 平台已实测会为同一手机号更换 uid，死拦会让真用户永远登不进去：
                // 亮出确认按钮，用户点「采用新 uid」才接受（回写 uidConfirmed=true）。
                if token == pendingToken { return }   // 同一 token 已在等确认，别重复刷状态
                pendingToken = token
                adoptButton.isHidden = false
                setStatus("⛔ 页面登录 uid=\(uid!)，与账号记录（\(expected)）不符。若这是你的账号（平台更换过 uid），点下方「采用新 uid 继续」；否则请退出后改用当前账号手机号登录。", .systemRed)
                return
            } else {
                // 登回正确账号：清掉等待确认状态
                pendingToken = nil
                adoptButton.isHidden = true
            }
        }
        readSessionThenFinish(token: token)
    }

    /// 用户确认「这是我的账号」（平台换发 uid 场景）：按确认的 token 收尾。
    @objc private func adoptUidClicked() {
        guard let token = pendingToken else { return }
        adoptButton.isHidden = true
        uidConfirmed = true
        setStatus("已确认采用新 uid，正在返回…", .systemGreen)
        readSessionThenFinish(token: token)
    }

    private func readSessionThenFinish(token: String) {
        webView.configuration.websiteDataStore.httpCookieStore.getAllCookies { [weak self] cookies in
            guard let self else { return }
            let session = cookies.first { $0.name == "X-Cloudide-Session" && $0.value.count > 10 }?.value
            self.resultToken = token
            self.resultSession = session
            if session == nil {
                self.setStatus("⚠ 未读到 X-Cloudide-Session：该登录态约 8 小时后就会失效且无法自动续期，"
                             + "建议关闭后重新登录一次。", .systemRed)
            } else {
                self.setStatus("登录成功，正在返回…", .systemGreen)
            }
            self.finish()
        }
    }

    private func finish() {
        guard !finished else { return }
        finished = true
        pollTimer?.invalidate()
        guard let token = resultToken else { exit(2) }
        // 先落盘再退，terminate 之后没有任何代码会执行。
        let ok = writeResult(opts.outFile, token: token, session: resultSession, uidConfirmed: uidConfirmed)
        exit(ok ? 0 : 4)
    }

    func windowWillClose(_ notification: Notification) {
        // 用户主动关窗 = 取消：不回写结果文件，主程序按「未取得」处理。
        pollTimer?.invalidate()
        exit(2)
    }
}

// MARK: - KnownDifferences（相对 Windows 宿主的有意差异）
//
// 1. 代理：不支持，见文件头「代理」小节的实测记录。
// 2. --user-data-dir：仅建目录以满足契约，浏览器数据用非持久化 store（见 buildUI 注释）。
// 3. --debug-capture：接口抓包逆向能力未移植，接受该参数但不产出抓包文件。

// MARK: - 入口

let args = Array(CommandLine.arguments.dropFirst())
guard let opts = parseArgs(args) else {
    FileHandle.standardError.write("参数错误：至少需要 --user-data-dir <dir> --out <file>\n".data(using: .utf8)!)
    exit(3)
}

if opts.selfTest {
    // 无 GUI 契约自检：不建窗口、不联网，直接回写一份固定结果，供 CI 校验进程/文件协议。
    let ok = writeResult(opts.outFile, token: "selftest-token-" + String(repeating: "x", count: 48),
                         session: "selftest-session")
    exit(ok ? 0 : 4)
}

if let probe = opts.jwtUidProbe {
    // 归属校验探针：把「页面登录的是另一个账号」这条防线的纯函数部分暴露出来，
    // 让 CI 能证明它真的在解析 data.id，而不是永远走 uid==nil 的放行分支。
    if let uid = parseAccountUid(probe) {
        print(uid)
    } else {
        print("<none>")
    }
    exit(0)
}

if let raw = opts.proxyServer, !raw.isEmpty {
    writeError(opts.outFile, "macOS 登录宿主不支持 --proxy-server（\(raw)）：WKWebView 没有可用的"
             + "连接级代理入口，实测 ProxySettings 两种写法都不生效。为避免你以为这个账号走了独立"
             + "代理、实际和其它账号同一个出口 IP，这里直接失败而不是静默直连。"
             + "签到/用量/GitHub 接口的代理不受影响，只有登录这一步需要直连网络。")
    exit(4)
}

try? FileManager.default.createDirectory(atPath: opts.userDataDir, withIntermediateDirectories: true)

_ = NSApplication.shared
NSApp.setActivationPolicy(.regular)
// 必须持有强引用：写成 HostController(opts).run() 的话控制器当场就被释放，
// 轮询定时器里的 weak self 恒为 nil，宿主会一直卡在「等待登录…」永不返回。
let hostController = HostController(opts)
hostController.run()
