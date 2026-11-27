#!/usr/bin/env bash
# macOS 产物的统一验收脚本。探路 workflow 和 Release job 都调它，
# 避免两份断言各改各的、最后 release 那边悄悄放宽。
#
# 用法: verify_macos_artifacts.sh <app_dir>
#   <app_dir> 里需同时有主程序 TraeTools 和登录宿主 TraeTools.LoginHost
set -u

APP_DIR="${1:?用法: verify_macos_artifacts.sh <app_dir>}"
APP="$APP_DIR/TraeTools"
HOST="$APP_DIR/TraeTools.LoginHost"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*"; exit 1; }
ok()   { echo "OK $*"; }

[ -x "$APP" ]  || fail "主程序不存在或不可执行：$APP"
[ -x "$HOST" ] || fail "登录宿主不存在或不可执行：$HOST"

# ---------- 1. 登录宿主进程契约 ----------
# 主程序只靠「命令行参数 + --out 回写 JSON」和宿主通信，这几条钉死的是那份契约。
"$HOST" >/dev/null 2>&1; rc=$?
[ "$rc" = "3" ] || fail "宿主无参调用应返回 3（参数错误），实际 $rc"
ok "宿主可执行，缺参数 → exit 3"

"$HOST" --user-data-dir "$WORK/ud" --out "$WORK/st.json" --selftest >/dev/null 2>&1; rc=$?
[ "$rc" = "0" ] || fail "宿主 --selftest 应返回 0，实际 $rc"
[ -f "$WORK/st.json" ] || fail "宿主 --selftest 没写结果文件"
python3 - "$WORK/st.json" <<'PY' || exit 1
import json, sys
d = json.load(open(sys.argv[1]))
assert isinstance(d.get("token"), str) and d["token"].strip(), "token 缺失或非字符串"
assert isinstance(d.get("session"), str), "session 缺失"
assert "error" not in d, "成功形态不该带 error"
PY
ok "宿主成功回写结构符合 LoginHostClient.ParseResult"

# 代理这条必须是「显式失败」而不是静默直连：静默直连会让用户以为多账号分网了，
# 实际全走同一个出口 IP，正是触发 9074 风控的条件。
# （WKWebView 在 macOS 上做不到连接级代理，两种 ProxySettings 写法已实测否掉。）
"$HOST" --user-data-dir "$WORK/ud" --out "$WORK/px.json" --proxy-server 127.0.0.1:7890 >/dev/null 2>&1; rc=$?
[ "$rc" = "4" ] || fail "宿主不支持代理时应返回 4，实际 $rc"
python3 - "$WORK/px.json" <<'PY' || exit 1
import json, sys
d = json.load(open(sys.argv[1]))
assert isinstance(d.get("error"), str) and d["error"], "必须回写 error 而不是假装成功"
assert "token" not in d
PY
ok "代理被显式拒绝（未静默直连）"

# ---------- 2. JWT 归属解析 ----------
# 证明「页面登录了另一个账号就拒关窗」这条防线真的在解析 data.id，
# 而不是解析失败后永远走放行分支。
JWT=$(python3 -c "
import base64, json
def b64(d): return base64.urlsafe_b64encode(json.dumps(d, separators=(',',':')).encode()).decode().rstrip('=')
print(b64({'alg':'HS256'}) + '.' + b64({'data':{'id':'7654321098765432'}}) + '.sig')
")
got=$("$HOST" --user-data-dir "$WORK/ud" --out "$WORK/p.json" --jwt-uid-probe "$JWT")
[ "$got" = "7654321098765432" ] || fail "JWT 归属解析应得 7654321098765432，实际 '$got'"
got=$("$HOST" --user-data-dir "$WORK/ud" --out "$WORK/p.json" --jwt-uid-probe "not-a-jwt")
[ "$got" = "<none>" ] || fail "非 JWT 应得 <none>，实际 '$got'"
ok "JWT 归属解析与 TokenUtils.ParseAccountUid 语义一致"

# ---------- 2.5 登录捕获链路端到端 ----------
# 前面验的是进程契约，这里验的是「真的能从 WKWebView 里把凭据捞出来」——
# 用本地假 Trae 页复现 localStorage token + X-Cloudide-Session cookie，
# 覆盖正常登录 / 跨账号拒收 / 无 session 三条路径。
bash "$(dirname "$0")/test_login_capture.sh" "$HOST" || fail "登录捕获链路端到端验证失败"

# ---------- 3. 凭据加密往返 ----------
# 换加密机制后最容易「静默坏掉」：Protect 成功但 Unprotect 取不回来，
# 界面上只表现为每次启动都要重新登录。
"$APP" --crypto-selftest > "$WORK/crypto.log" 2>&1
grep -q "CRYPTO SELFTEST: PASS" "$WORK/crypto.log" || { cat "$WORK/crypto.log"; fail "凭据加密自检未通过"; }
grep -q "mechanism:" "$WORK/crypto.log" && sed -n 's/^mechanism: //p' "$WORK/crypto.log" | head -1
ok "凭据加密往返正常"

# ---------- 4. 开机自启 ----------
# plist 文件存在不等于格式合法：格式错了 launchd 静默忽略，用户端就是「勾了没反应」。
"$APP" --autostart-selftest > "$WORK/autostart.log" 2>&1
grep -q "AUTOSTART SELFTEST: PASS" "$WORK/autostart.log" || { cat "$WORK/autostart.log"; fail "自启往返自检未通过"; }
sed -n '/\[autostart\] begin-content/,/\[autostart\] end-content/p' "$WORK/autostart.log" \
  | sed '1d;$d' > "$WORK/launchagent.plist"
if [ -s "$WORK/launchagent.plist" ]; then
  plutil -lint "$WORK/launchagent.plist" >/dev/null || { cat "$WORK/launchagent.plist"; fail "LaunchAgent plist 格式非法"; }
  grep -q "/usr/bin/open" "$WORK/launchagent.plist" \
    || fail "LaunchAgent 未走 open -a：直接指向包内二进制会绕过 LaunchServices 丢掉 bundle 身份"
  ok "LaunchAgent plist 合法且走 open -a"
else
  # Windows 上没有 plist；本脚本目前只在 macOS runner 上调用，走到这里说明自检没产出。
  fail "自启处于打开态却没打印出 plist 内容"
fi

# ---------- 5. 切换模块的相对路径解析 ----------
# 白名单用反斜杠作比较键，反斜杠在 mac 上是合法文件名而非分隔符：解析错了不报错，
# 只会「切换报成功但一个文件都没动」。这条是无客户端环境下唯一能自动验证的一环。
"$APP" --switch-path-selftest > "$WORK/swpath.log" 2>&1
grep -q "SWITCH PATH SELFTEST: PASS" "$WORK/swpath.log" || { cat "$WORK/swpath.log"; fail "切换路径解析自检未通过"; }
grep "白名单" "$WORK/swpath.log" || true
ok "切换相对路径解析正常（含「未创建的边车必须判不存在」的反向对照）"

# ---------- 5.1 账号独立数据目录（跨平台核心逻辑） ----------
# 新建档案会重写 machineid 并清空 aha，这两个动作一旦在 mac 上路径解析错，
# 结果是「两个账号设备指纹相同」或「切过去未登录」，且不会报错。
"$APP" --profile-selftest > "$WORK/profile.log" 2>&1
grep -q "PROFILE SELFTEST: PASS" "$WORK/profile.log" || { cat "$WORK/profile.log"; fail "账号独立目录自检未通过"; }
cat "$WORK/profile.log"
ok "账号独立目录：machineid 唯一、aha 已清空、启动参数指向各自目录"

# ---------- 5.2 保活请求体（跨平台，纯离线） ----------
"$APP" --keepalive-selftest > "$WORK/keepalive.log" 2>&1
grep -q "KEEPALIVE SELFTEST: PASS" "$WORK/keepalive.log" || { cat "$WORK/keepalive.log"; fail "保活请求体自检未通过"; }
ok "保活请求体字段与模型默认值正确"

# ---------- 3.5 网络与 TLS ----------
# 签到/用量/云端全走 https://api.trae.cn。自包含 macOS 包的证书链校验走 .NET +
# Security.framework，坏掉时表现是「签到失败：SSL/信任」，与代码逻辑无关、只在真机暴露。
# 连不上只算 unknown（CI 机器可能到不了国内站点），TLS 层失败才是硬失败。
"$APP" --net-selftest > "$WORK/net.log" 2>&1
rc=$?
cat "$WORK/net.log"
[ "$rc" = "0" ] || fail "TLS 自检失败（见上方 net.log）"
grep -q "NET SELFTEST: PASS" "$WORK/net.log" || fail "TLS 自检没有明确结论"
ok "TLS 信任链在本平台可用（api.trae.cn 可达性见上方日志，不计入判定）"

# ---------- 6. 原生依赖库是否随包 ----------
# 自包含发布靠 NuGet 的 runtime assets 带出这些 .dylib。缺了不会编译失败，
# 而是第一次用到该功能时才崩，且只在真机上暴露。
# SQLite 已由冒烟间接证明（CheckinDatabase 构造即 Open+建表），这里补没被自检覆盖的。
echo "--- 包内原生库 ---"
find "$(dirname "$APP")" -maxdepth 1 -name '*.dylib' -exec basename {} \; | sort | sed 's/^/  /'
for lib in libSkiaSharp.dylib libAvaloniaNative.dylib libHarfBuzzSharp.dylib \
           libe_sqlite3.dylib libsodium.dylib; do
  [ -f "$(dirname "$APP")/$lib" ] || fail "包内缺少原生库：$lib"
done
ok "原生库齐全（Skia/Avalonia/SQLite/Sodium/HarfBuzz）"

# ---------- 7. 应用内更新的换包逻辑 ----------
# 更新器是唯一能把用户现有安装直接搞坏的模块。这里跑真实的替换脚本与真实的
# hdiutil/ditto，重点验两条失败路径：放入失败要能回滚、缺登录宿主的包要拒绝安装。
"$APP" --update-swap-selftest > "$WORK/swap.log" 2>&1
rc=$?
cat "$WORK/swap.log"
[ "$rc" = "0" ] || fail "换包自检失败（见上方 swap.log）"
grep -q "UPDATE SWAP SELFTEST: PASS" "$WORK/swap.log" || fail "换包自检没有明确结论"
ok "更新换包/回滚/解包/缺宿主拒装 四条路径正常"

echo "=== macOS 产物验收全部通过 ==="
