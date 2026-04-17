#!/bin/bash
# QuickSL 多平台构建脚本
# 用法: ./build_all.sh [win|mac|all]
#
# 前提: 需要先构建好 QuickSL.dll（通过 Godot 编辑器或 dotnet build QuickSL/）

set -e

PUBLISH_DIR="publish"

build_win() {
    echo "══════════════════════════════════════════"
    echo "  构建 Windows (win-x64) 安装器..."
    echo "══════════════════════════════════════════"
    dotnet publish QuickSL.Installer/QuickSL.Installer.csproj \
        -c Release -r win-x64 \
        -o "$PUBLISH_DIR/win-x64" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true
    echo "✅ Windows 安装器: $PUBLISH_DIR/win-x64/QuickSL_Setup.exe"
    echo ""
}

build_mac_arm() {
    echo "══════════════════════════════════════════"
    echo "  构建 macOS (Apple Silicon) 安装器..."
    echo "══════════════════════════════════════════"
    dotnet publish QuickSL.Installer/QuickSL.Installer.csproj \
        -c Release -r osx-arm64 \
        -o "$PUBLISH_DIR/osx-arm64" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true
    chmod +x "$PUBLISH_DIR/osx-arm64/QuickSL_Setup"
    echo "✅ macOS (ARM64) 安装器: $PUBLISH_DIR/osx-arm64/QuickSL_Setup"
    echo ""
}

build_mac_x64() {
    echo "══════════════════════════════════════════"
    echo "  构建 macOS (Intel) 安装器..."
    echo "══════════════════════════════════════════"
    dotnet publish QuickSL.Installer/QuickSL.Installer.csproj \
        -c Release -r osx-x64 \
        -o "$PUBLISH_DIR/osx-x64" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true
    chmod +x "$PUBLISH_DIR/osx-x64/QuickSL_Setup"
    echo "✅ macOS (x64) 安装器: $PUBLISH_DIR/osx-x64/QuickSL_Setup"
    echo ""
}

case "${1:-all}" in
    win)     build_win ;;
    mac)     build_mac_arm ;;
    mac-x64) build_mac_x64 ;;
    all)
        build_win
        build_mac_arm
        build_mac_x64
        echo "══════════════════════════════════════════"
        echo "  全部构建完成！"
        echo "══════════════════════════════════════════"
        ls -lh "$PUBLISH_DIR"/*/QuickSL_Setup* 2>/dev/null || true
        ;;
    *)
        echo "用法: $0 [win|mac|mac-x64|all]"
        exit 1
        ;;
esac
