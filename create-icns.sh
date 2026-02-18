#!/bin/bash

# 将 PNG 图标转换为 macOS .icns 格式
# 使用方法: ./create-icns.sh

SOURCE_PNG="Assets/Icons/icon.png"
OUTPUT_ICNS="Assets/Icons/icon.icns"

if [ ! -f "$SOURCE_PNG" ]; then
    echo "错误: 找不到源图标文件 $SOURCE_PNG"
    exit 1
fi

echo "创建 macOS .icns 图标..."

# 创建临时目录
ICONSET_DIR="icon.iconset"
rm -rf "$ICONSET_DIR"
mkdir "$ICONSET_DIR"

# 生成不同尺寸的图标
# macOS 需要以下尺寸：16, 32, 64, 128, 256, 512, 1024
# 每个尺寸都需要 1x 和 2x (@2x) 版本

echo "生成各种尺寸的图标..."
sips -z 16 16     "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_16x16.png" > /dev/null
sips -z 32 32     "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_16x16@2x.png" > /dev/null
sips -z 32 32     "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_32x32.png" > /dev/null
sips -z 64 64     "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_32x32@2x.png" > /dev/null
sips -z 128 128   "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_128x128.png" > /dev/null
sips -z 256 256   "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_128x128@2x.png" > /dev/null
sips -z 256 256   "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_256x256.png" > /dev/null
sips -z 512 512   "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_256x256@2x.png" > /dev/null
sips -z 512 512   "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_512x512.png" > /dev/null
sips -z 1024 1024 "$SOURCE_PNG" --out "${ICONSET_DIR}/icon_512x512@2x.png" > /dev/null

# 转换为 .icns
echo "转换为 .icns 格式..."
iconutil -c icns "$ICONSET_DIR" -o "$OUTPUT_ICNS"

# 清理临时文件
rm -rf "$ICONSET_DIR"

echo "✅ 图标创建完成: $OUTPUT_ICNS"
ls -lh "$OUTPUT_ICNS"
