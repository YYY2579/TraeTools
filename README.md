# TraeTools

Trae 多账号管理工具：每日签到、云端托管签到、账号切换、用量统计。

> 基于 [star620/TraeTools](https://github.com/star620/TraeTools) 二次开发，持续修 Bug、补功能。
> 觉得有用就点个 **Star ⭐**，这是最直接的鼓励。

---

## 下载

最新版本见 [Releases](https://github.com/YYY2579/TraeTools/releases)。

| 平台 | 文件 | 说明 |
|---|---|---|
| Windows x64 | `TraeTools-win-x64.msi` | **推荐**。安装到 Program Files，带开始菜单/桌面快捷方式与卸载项 |
| Windows x64 | `TraeTools-win-x64.zip` | 免安装整包。应用内更新用它，一般无需手动下载 |
| macOS Apple Silicon | `TraeTools-macos-arm64.dmg` | M1 / M2 / M3 / M4 |
| macOS Intel | `TraeTools-macos-x64.dmg` | x86_64 机型 |
| macOS 菜单栏版 | `TraeBar-macos-arm64.dmg` / `-x64.dmg` | 轻量版，无窗口，只在菜单栏做自动签到 |

**macOS 有两个程序，别装错**：`TraeTools-*` 是完整窗口版（功能与 Windows 对齐），`TraeBar-*` 是菜单栏轻量版（约 0.2 MB，只做签到）。芯片不确定就点  →「关于本机」看「芯片」那一行。

### 安装

- **Windows**：双击 MSI，需要管理员权限；已装旧版会直接覆盖升级。免安装用户解压 zip 双击 `TraeTools.exe`，无需 .NET 运行时。
- **macOS**：打开 dmg，把 App 拖进「应用程序」（**别在 dmg 里直接双击**，那样关掉就没了）。未做 Apple 公证，首次启动需放行一次：

  ```bash
  xattr -cr /Applications/TraeTools.app
  ```

  或在「系统设置 → 隐私与安全性」底部点「仍要打开」。放行只需一次。

账号数据在用户目录（Windows `%APPDATA%\TraeTools`，macOS `~/Library/Application Support/TraeTools`），卸载不删除。

---

## 功能

- **每日签到**：多账号管理，一键签到全部启用账号；可设时间自动签到
- **云端签到**：一键部署到自己的 GitHub fork，由 Actions 定时运行，不用开机；运行时间可自定义
- **失败告警**：过了签到时间 10 分钟仍有账号未签，自动推送告警（含逐账号状态与处理建议）
- **账号切换**：备份登录态后一键冷切换，失败自动回滚（**仅 Windows**）
- **用量统计**：按会话展示模型 / Token / 缓存命中 / 积分消耗，含积分快过期提醒与过期日志
- **推送渠道**：飞书、钉钉、企业微信、Telegram、自定义 Webhook、邮件，另支持系统通知
- **网络代理**：全局代理，可按账号覆盖，便于多账号分流降风控
- **主题 / 更新**：明暗跟随系统；内置检查更新与静默替换（失败回滚）
- **安全检查**：一键核对凭据是否加密落盘、有无明文残留

## 平台差异

| 功能 | Windows | macOS |
|---|---|---|
| 签到 / 用量 / 云端签到 / 推送 / 自动更新 | ✅ | ✅ |
| 网络代理 | 全局 + 每账号，含登录浏览器 | 签到 / 用量 / GitHub 生效，登录浏览器不支持 |
| 账号切换 | ✅ | ❌ 尚未开放 |
| 凭据存储 | Windows DPAPI | AES-256-GCM + 本机密钥文件 |

两端都只在本机可解：把配置目录拷到另一台电脑会解不开，表现为需重新登录（属预期）。

---

## 从源码运行

需要 .NET 10 SDK。

```bash
git clone https://github.com/YYY2579/TraeTools.git
cd TraeTools
dotnet build && dotnet run
```

打包：

```bash
# Windows 免安装整包（应用内更新器用的就是它）
dotnet publish TraeTools.csproj -c Release -r win-x64 --self-contained true -p:EnableAot=true -o publish_aot

# Windows 安装包（先装 WiX：dotnet tool install --global wix --version 5.0.2）
wix build installer/TraeTools.wxs -arch x64 -d PublishDir=publish_aot -d ProductVersion=1.5.9 -o TraeTools-win-x64.msi

# macOS 按架构编译
dotnet publish TraeTools.csproj -c Release -r osx-arm64 --self-contained true   # Apple Silicon
dotnet publish TraeTools.csproj -c Release -r osx-x64   --self-contained true   # Intel
```

### 无界面自检

不启动窗口也能验证关键链路，本地和 CI 都能跑：

```bash
TraeTools --crypto-selftest        # 凭据加解密往返
TraeTools --log-selftest           # 日志：内存缓冲 / 落盘 / 回填去重
TraeTools --diagnose-selftest      # 云端失败原因归类
TraeTools --switch-path-selftest   # 切换模块路径解析
TraeTools --autostart-selftest     # 开机自启读写
TraeTools --net-selftest           # TLS 信任链
```

`--notify-selftest` 会用环境变量里的 SMTP 配置真实发一封告警邮件（凭据不落盘、不进仓库），变量：`TTSMTP_HOST` / `TTSMTP_PORT` / `TTSMTP_SEC` / `TTSMTP_USER` / `TTSMTP_PASS` / `TTSMTP_TO`。

---

## 常见问题

**签到报 9074「参与用户太多」**
设备号被风控。设置页「更换设备号」后重试；云端签到需重新部署。

**签到报「会话失效」**
Session 约 14 天过期。重新登录 Trae；云端还需再点一次「重新部署」把新 Session 写回 Secret。

**云端定时任务没跑或失败**
到 fork 仓库的 Actions 页看日志。常见原因是 Session 过期或整点高峰风控，可错开时间、减少云端账号数。

**为什么没收到失败通知**
检查设置页各渠道开关。失败告警不受「签到完成后推送」总开关限制，但需要至少启用一个渠道（或打开系统通知）。

**macOS 提示「已损坏，无法打开」**
未公证导致，按上面的 `xattr -cr` 放行即可，文件没坏。

---

## 开发者

- 技术栈：Avalonia 12 / .NET 10 / CommunityToolkit.Mvvm / Sodium.Core
- 登录宿主按平台各一个独立进程：Windows 用 WebView2（`TraeTools.LoginHost/`，C#），macOS 用 WKWebView（`macos-loginhost/`，Swift）。两者只通过「命令行参数 + `--out` 回写 JSON」与主程序通信
- 目录：`ViewModels/` 页面逻辑 · `Services/` 网络与领域服务 · `Views/` 界面 · `Models/` 数据模型 · `installer/` Windows MSI 定义
- Windows 与 macOS 共用同一份代码、按 RID 编译，平台差异用 `OperatingSystem.IsWindows()` 分支
- 菜单栏轻量版 TraeBar 为 Swift/AppKit，在 `macos` 分支，与主项目独立发布
- 发布：`release.yml` 出三平台产物（Windows MSI + zip、macOS 双架构 dmg、TraeBar），macOS 另跑契约验收（登录宿主协议、加密往返、自启、更新换包回滚、TLS）

> 注意：Avalonia 桌面版的 macOS 编译目前只在发版流水线做，`ci.yml` 的 mac 任务只编 TraeBar。改跨平台代码后建议本地先 `dotnet build -r osx-arm64`，或跑一次「只构建不发布」的发版空跑。

---

## 免责声明

- 个人开源项目，与 ByteDance / Trae 官方无关
- 使用签到、账号切换等功能请遵守 Trae 服务条款，风险自负
- 请勿出售、外借或用于非法用途；凭证只保存在你自己的电脑与 GitHub Secret 中

---

## 关于作者

**我不懂代码，这个项目是我和 AI 一起做的** —— 我提需求、描述现象，代码由 AI 写。

所以：**欢迎自己 fork 下来喂给 AI 二开**。普通 C#/.NET + Avalonia，AI 改起来没什么障碍；仓库里留了自检脚本与验收脚本，改完能自己验证。GPL v3，改完记得开源。

用不了、报错了 → 提 [Issue](https://github.com/YYY2579/TraeTools/issues)，带上「运行日志」页的相关条目和左下角版本号。加了觉得好用的功能 → 欢迎提 PR，哪怕是你和 AI 一起弄的。

谢谢 [star620](https://github.com/star620) 开源前身项目，谢谢 TRAE-Checkin、Trea-Switch 的作者，也谢谢每一个提 Issue、提 PR、帮我纠错的人。

## 许可证

GPL v3，见 [LICENSE](LICENSE)。
