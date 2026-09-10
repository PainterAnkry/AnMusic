#!/usr/bin/env bash
# AnMusic 安卓端构建脚本
#
# 背景：本仓库所在环境的 shell 不继承 Windows 标准环境变量（APPDATA / PROCESSOR_ARCHITECTURE 等），
# 而 dotnet 的 workload 安装器与 NuGet 都会在静态初始化时读取它们，缺失即报
#   "Value cannot be null. (Parameter 'path1')"
# 因此所有 dotnet 命令都必须先加载下面的环境。
#
# 用法：
#   ./build-android.sh              # Debug 构建（默认，出可安装 APK）
#   ./build-android.sh release      # 尝试 Release 构建
#   ./build-android.sh install      # 构建后用 adb 安装到已连接的设备

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${ANMUSIC_ENV_FILE:-C:/Users/Ankry/AndroidTools/env.sh}"

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  source "$ENV_FILE"
else
  echo "警告：未找到环境文件 $ENV_FILE，尝试使用系统既有环境变量。" >&2
  echo "       如构建报 'path1' 错误，请参照 env.sh 补齐 Windows 标准变量。" >&2
fi

MODE="${1:-debug}"
PROJECT="$REPO_ROOT/src/AnMusic.Android/AnMusic.Android.csproj"

case "$MODE" in
  debug)
    echo "==> Debug 构建"
    dotnet build "$PROJECT" -c Debug
    OUT_DIR="$REPO_ROOT/src/AnMusic.Android/bin/Debug/net10.0-android"
    echo "==> 产物：$OUT_DIR/com.painterankry.anmusic-Signed.apk"
    ;;

  release)
    echo "==> Release 构建"
    # 注意：Release 需要 Microsoft.NETCore.App.Runtime.Mono.win-x64。
    # 该包在 nuget.org 上只发布到 9.0 预览版，10.x 仅通过 MSI 随工作负载分发
    # （见 dotnet/metadata/workloads/<band>/installertype = msi）。
    # 若本机是 MSI 方式安装工作负载则 Release 可正常构建；
    # 用 --skip-manifest-update 装的轻量工作负载会在此步失败，改用 Debug 即可。
    dotnet build "$PROJECT" -c Release
    OUT_DIR="$REPO_ROOT/src/AnMusic.Android/bin/Release/net10.0-android"
    echo "==> 产物目录：$OUT_DIR"
    ;;

  install)
    "$0" debug
    APK="$REPO_ROOT/src/AnMusic.Android/bin/Debug/net10.0-android/com.painterankry.anmusic-Signed.apk"
    echo "==> 安装到设备：$APK"
    "$ANDROID_HOME/platform-tools/adb.exe" install -r "$APK"
    ;;

  *)
    echo "未知参数：$MODE（可选：debug / release / install）" >&2
    exit 1
    ;;
esac
