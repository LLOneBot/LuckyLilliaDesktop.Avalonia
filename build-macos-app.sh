#!/bin/bash

# 构建 macOS .app 应用程序包
# 使用方法: ./build-macos-app.sh [arch]
# 示例: ./build-macos-app.sh arm64  或  ./build-macos-app.sh x64

ARCH=${1:-arm64}  # 默认 arm64

if [[ "$ARCH" != "arm64" && "$ARCH" != "x64" ]]; then
    echo "错误: 架构必须是 arm64 或 x64"
    exit 1
fi

APP_NAME="LuckyLilliaDesktop"
RID="osx-$ARCH"
VERSION="2.5.0"

echo "========================================="
echo "构建 macOS .app 应用程序包"
echo "架构: $ARCH"
echo "RID: $RID"
echo "========================================="

# 1. 发布应用
echo ""
echo "步骤 1/4: 发布应用..."
dotnet publish -c Release -r "$RID" --self-contained true

if [ $? -ne 0 ]; then
    echo "错误: 发布失败"
    exit 1
fi

# 2. 创建 .app 目录结构
echo ""
echo "步骤 2/4: 创建 .app 目录结构..."
APP_DIR="bin/Release/net9.0/$RID/$APP_NAME.app"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Resources"

# 3. 复制发布的文件到 .app 中
echo ""
echo "步骤 3/4: 复制应用文件..."
cp -r "bin/Release/net9.0/$RID/publish/"* "$APP_DIR/Contents/MacOS/"

# 4. 创建 Info.plist
echo ""
echo "步骤 4/4: 创建 Info.plist..."
cat > "$APP_DIR/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>Lucky Lillia Desktop</string>
    <key>CFBundleIdentifier</key>
    <string>com.luckylillia.desktop</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundleIconFile</key>
    <string>icon.icns</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
EOF

# 复制图标（如果存在）
if [ -f "Assets/Icons/icon.icns" ]; then
    cp "Assets/Icons/icon.icns" "$APP_DIR/Contents/Resources/"
fi

# 设置可执行权限
chmod +x "$APP_DIR/Contents/MacOS/$APP_NAME"

echo ""
echo "========================================="
echo "✅ 构建完成!"
echo "应用路径: $APP_DIR"
echo "========================================="
echo ""
echo "测试运行: open \"$APP_DIR\""
echo "或直接双击打开: $APP_DIR"
