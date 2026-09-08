#!/usr/bin/env bash
# 在 WSL 里构建(调用 Windows 侧的 csc.exe, 产物落在 dist/ 下)
# 纯 Windows 用户请用: powershell -ExecutionPolicy Bypass -File build.ps1
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
[ -x "$CSC" ] || CSC="/mnt/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe"
[ -x "$CSC" ] || { echo "找不到 csc.exe" >&2; exit 1; }

mkdir -p "$ROOT/dist"
WROOT="$(wslpath -w "$ROOT")"
SRCS=()
for f in "$ROOT"/src/*.cs; do SRCS+=("$(wslpath -w "$f")"); done

"$CSC" /nologo /target:winexe /optimize+ \
  "/out:${WROOT}\\dist\\DSH-Launcher.exe" \
  "/win32manifest:${WROOT}\\app.manifest" \
  "/win32icon:${WROOT}\\icon\\dsh-launcher.ico" \
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll \
  "${SRCS[@]}"

echo "OK -> $ROOT/dist/DSH-Launcher.exe"
