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
#   ./build-android.sh sign         # 构建 + 用项目密钥库重签并输出到 installer/
#   ./build-android.sh install      # 构建后用 adb 安装到已连接的设备
#
# 环境变量（sign 用，均有默认值）：
#   ANMUSIC_KEYSTORE  密钥库路径，默认 C:/Users/Ankry/AndroidTools/anmusic.keystore
#   ANMUSIC_KEY_ALIAS 别名，默认 anmusic
#   ANMUSIC_KEY_PASS  密钥库口令，默认 anmusic123

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${ANMUSIC_ENV_FILE:-C:/Users/Ankry/AndroidTools/env.sh}"

# Git Bash 的 pwd 给出的是 /c/Users/... 形式，MSBuild / apksigner 这类
# Windows 原生程序不认，必须转成 C:\Users\... 再传过去。
win_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s' "$1"
  fi
}

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  source "$ENV_FILE"
else
  echo "警告：未找到环境文件 $ENV_FILE，尝试使用系统既有环境变量。" >&2
  echo "       如构建报 'path1' 错误，请参照 env.sh 补齐 Windows 标准变量。" >&2
fi

MODE="${1:-debug}"
PROJECT_REL="src/AnMusic.Android/AnMusic.Android.csproj"
PROJECT="$REPO_ROOT/$PROJECT_REL"
DEBUG_OUT="$REPO_ROOT/src/AnMusic.Android/bin/Debug/net10.0-android"

# 从 csproj 读版本号，避免产物名与工程版本脱节
read_version() {
  sed -n 's:.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*:\1:p' \
    "$PROJECT" | head -1 | tr -d '[:space:]'
}

# 统一在仓库根目录执行，给 dotnet 传相对路径最稳（Git Bash 会自行转换）
cd "$REPO_ROOT"

do_debug() {
  echo "==> Debug 构建"
  dotnet build "$PROJECT_REL" -c Debug
}

# 用项目密钥库签名。
# 注意：dotnet build 产出的 *-Signed.apk 用的是 Android **调试**密钥，
# 不能用于发布，这里必须重签。
do_sign() {
  do_debug

  local version keystore alias pass apk out
  version="$(read_version)"
  keystore="${ANMUSIC_KEYSTORE:-C:/Users/Ankry/AndroidTools/anmusic.keystore}"
  alias="${ANMUSIC_KEY_ALIAS:-anmusic}"
  pass="${ANMUSIC_KEY_PASS:-anmusic123}"

  apk="$DEBUG_OUT/com.painterankry.anmusic.apk"
  out="$REPO_ROOT/installer/AnMusic-Android-${version}-dev.apk"

  if [[ ! -f "$apk" ]]; then
    echo "错误：未找到构建产物 $apk" >&2
    exit 1
  fi
  if [[ ! -f "$keystore" ]]; then
    echo "错误：未找到密钥库 $keystore" >&2
    exit 1
  fi

  echo "==> 用项目密钥库签名 v$version -> $out"
  "$ANDROID_HOME/build-tools/36.0.0/apksigner.bat" sign \
    --ks "$(win_path "$keystore")" \
    --ks-key-alias "$alias" \
    --ks-pass "pass:$pass" \
    --key-pass "pass:$pass" \
    --out "$(win_path "$out")" \
    "$(win_path "$apk")"

  echo "==> 验证签名"
  "$ANDROID_HOME/build-tools/36.0.0/apksigner.bat" verify --print-certs "$(win_path "$out")" | head -2
  ls -la "$out"
}

case "$MODE" in
  debug)
    do_debug
    echo "==> 产物：$DEBUG_OUT/com.painterankry.anmusic-Signed.apk"
    echo "    注意：这个是调试密钥签的，发布前请用 ./build-android.sh sign 重签。"
    ;;

  release)
    echo "==> Release 构建"
    # 注意：Release 需要 Microsoft.NETCore.App.Runtime.Mono.win-x64。
    # 该包在 nuget.org 上只发布到 9.0 预览版，10.x 仅通过 MSI 随工作负载分发
    # （见 dotnet/metadata/workloads/<band>/installertype = msi）。
    # 若本机是 MSI 方式安装工作负载则 Release 可正常构建；
    # 用 --skip-manifest-update 装的轻量工作负载会在此步失败，改用 Debug 即可。
    dotnet build "$PROJECT_REL" -c Release
    echo "==> 产物目录：$REPO_ROOT/src/AnMusic.Android/bin/Release/net10.0-android"
    ;;

  sign)
    do_sign
    ;;

  install)
    do_debug
    APK="$DEBUG_OUT/com.painterankry.anmusic-Signed.apk"
    echo "==> 安装到设备：$APK"
    "$ANDROID_HOME/platform-tools/adb.exe" install -r "$(win_path "$APK")"
    ;;

  *)
    echo "未知参数：$MODE（可选：debug / release / sign / install）" >&2
    exit 1
    ;;
esac
