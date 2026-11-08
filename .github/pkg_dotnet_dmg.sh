#!/usr/bin/env bash
# 把 dotnet publish 的产物组装成 TraeTools.app 并打成 .dmg。
# 用法: pkg_dotnet_dmg.sh <arm64|x86_64> <version> [publish_dir]
#
# 与 macos 分支 pkg_dmg.sh（Swift 菜单栏版 TraeBar）是两条独立产品线，
# 那个是 LSUIElement 无窗口，这个是完整窗口应用，不要混用。
#
# 布局说明：.NET 的 apphost 只在自己**同目录**找 TraeTools.dll，
# 所以整套 publish 产物直接放 Contents/MacOS/，不能图干净挪去 Resources/app/。
set -euo pipefail

ARCH="${1:?用法: pkg_dotnet_dmg.sh <arm64|x86_64> <version> [publish_dir]}"
VERSION="${2:-dev}"
PUBLISH_DIR="${3:-out-macos}"

LABEL="$ARCH"
[ "$ARCH" = "x86_64" ] && LABEL="x64"
# 卷名用人类可读的芯片名，用户在访达/磁盘工具里一眼能看出装的是哪个架构
CHIP="Apple Silicon"
[ "$ARCH" = "x86_64" ] && CHIP="Intel"
APP=TraeTools.app
BIN_DIR="$APP/Contents/MacOS"

[ -d "$PUBLISH_DIR" ] || { echo "找不到发布目录：$PUBLISH_DIR" >&2; exit 1; }
[ -f "$PUBLISH_DIR/TraeTools" ] || { echo "找不到主程序：$PUBLISH_DIR/TraeTools" >&2; exit 1; }

rm -rf "$APP" dmg-stage
mkdir -p "$BIN_DIR" "$APP/Contents/Resources"

# ---- 应用图标：Swift + AppKit 现场绘制（圆角深色底 + 青色 T）----
# 没有图标时 macOS 会给一个通用白纸图标，菜单栏也会显示成 "Avalonia Application"。
cat > make_icon.swift << 'EOF'
import AppKit
let size: CGFloat = 1024
let img = NSImage(size: NSSize(width: size, height: size))
img.lockFocus()
let bg = NSBezierPath(roundedRect: NSRect(x: 64, y: 64, width: size - 128, height: size - 128), xRadius: 200, yRadius: 200)
NSColor(calibratedWhite: 0.09, alpha: 1).setFill()
bg.fill()
if let grad = NSGradient(colors: [NSColor(calibratedWhite: 0.13, alpha: 1), NSColor(calibratedWhite: 0.05, alpha: 1)]) {
    grad.draw(in: bg, angle: -90)
}
let para = NSMutableParagraphStyle()
para.alignment = .center
let attrs: [NSAttributedString.Key: Any] = [
    .font: NSFont.systemFont(ofSize: 620, weight: .bold),
    .foregroundColor: NSColor(calibratedRed: 0.13, green: 0.83, blue: 0.92, alpha: 1),
    .paragraphStyle: para,
]
let str = NSAttributedString(string: "T", attributes: attrs)
str.draw(in: NSRect(x: 0, y: size / 2 - 460, width: size, height: 920))
img.unlockFocus()
let rep = NSBitmapImageRep(data: img.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "icon_1024.png"))
EOF
swift make_icon.swift
mkdir -p AppIcon.iconset
for s in 16 32 64 128 256 512; do
  sips -z "$s" "$s" icon_1024.png --out "AppIcon.iconset/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2))
  sips -z "$d" "$d" icon_1024.png --out "AppIcon.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns AppIcon.iconset -o AppIcon.icns
cp AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

# ---- Info.plist ----
# 刻意不写 LSUIElement：这是有主窗口的完整应用，与 TraeBar 菜单栏版相反。
# NSPrincipalClass 必须有：缺了它 LaunchServices 不把这个包当标准 NSApplication 接管，
# 加粗的应用菜单名就不会取 CFBundleName，实测会退化成 Avalonia 的 "Avalonia Application"。
cat > "$APP/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>TraeTools</string>
  <key>CFBundleDisplayName</key><string>TraeTools</string>
  <key>CFBundleIdentifier</key><string>com.traetools.desktop</string>
  <key>CFBundleExecutable</key><string>TraeTools</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION#v}</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# ---- 整套 publish 产物进 MacOS/ ----
cp -R "$PUBLISH_DIR"/. "$BIN_DIR"/
chmod +x "$BIN_DIR/TraeTools"

# ---- 登录宿主：主程序在 AppContext.BaseDirectory（即 MacOS/）找它 ----
# 缺宿主会让自动登录静默退化成「手动粘贴 Cookie」，属于功能缺失而不是可选优化，
# 所以默认直接失败；只有本地调试才用 ALLOW_MISSING_LOGIN_HOST=1 跳过。
HOST_SRC="$PUBLISH_DIR/TraeTools.LoginHost"
if [ -f "$HOST_SRC" ]; then
  chmod +x "$HOST_SRC"
elif [ "${ALLOW_MISSING_LOGIN_HOST:-0}" = "1" ]; then
  echo "警告：缺登录宿主，已按 ALLOW_MISSING_LOGIN_HOST=1 放行" >&2
else
  echo "找不到登录宿主：$HOST_SRC" >&2
  echo "先跑 .github/build_login_host.sh <arch> $PUBLISH_DIR；本地调试可设 ALLOW_MISSING_LOGIN_HOST=1" >&2
  exit 1
fi

# ---- ad-hoc 签名：先签内层宿主，再签整个 bundle ----
# 顺序不能反：签完外层再改内层文件会让 seal 失效，直接报「已损坏」。
codesign --force --sign - "$BIN_DIR/TraeTools.LoginHost" 2>/dev/null || true
codesign --force --deep --sign - "$APP"

# ---- dmg ----
mkdir -p dmg-stage
cp -R "$APP" dmg-stage/
ln -s /Applications dmg-stage/Applications
cat > "dmg-stage/首次启动必读.txt" << 'READMEE'
【TraeTools 首次启动放行（只需一次）】

macOS 会拦截一切从网络下载的未公证软件，这与软件本身无关。放行方式二选一：

方式一（推荐）：打开终端，执行：
  xattr -cr /Applications/TraeTools.app

方式二：系统设置 -> 隐私与安全性 -> 拉到底部，点击「仍要打开」。

之后即可正常双击使用。本版本是完整窗口应用（有 Dock 图标），
与菜单栏常驻的 TraeBar 是两个不同的程序，可同时安装。
READMEE
hdiutil create -volname "TraeTools (${CHIP})" -srcfolder dmg-stage -ov -format UDZO "TraeTools-macos-${LABEL}.dmg"

# 保留 .app 供后续冒烟测试从包内启动；只清中间产物。
rm -rf dmg-stage AppIcon.iconset icon_1024.png make_icon.swift AppIcon.icns
echo "产出：TraeTools-macos-${LABEL}.dmg（.app 保留在当前目录）"
