#!/bin/bash
# LiquidUnzip 构建脚本（csc.exe + GAC WPF 引用，无需 .NET SDK）
set -e

CSC="C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
GAC="C:/Windows/Microsoft.NET/assembly"

R_PF="$GAC/GAC_MSIL/PresentationFramework/v4.0_4.0.0.0__31bf3856ad364e35/PresentationFramework.dll"
R_PC="$GAC/GAC_64/PresentationCore/v4.0_4.0.0.0__31bf3856ad364e35/PresentationCore.dll"
R_WB="$GAC/GAC_MSIL/WindowsBase/v4.0_4.0.0.0__31bf3856ad364e35/WindowsBase.dll"
R_XAML="$GAC/GAC_MSIL/System.Xaml/v4.0_4.0.0.0__b77a5c561934e089/System.Xaml.dll"

REFS="/r:$R_PF /r:$R_PC /r:$R_WB /r:$R_XAML"

ROOT="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null || cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/src"
OUT="${1:-$ROOT/dist}"

# 归一化为 Windows 路径，避免 MSYS 路径被 csc 误解析
ROOT_WIN="$(cygpath -w "$ROOT" 2>/dev/null || echo "$ROOT")"
OUT_WIN="$(cygpath -w "$OUT" 2>/dev/null || echo "$OUT")"
SRC_WIN="$(cygpath -w "$SRC" 2>/dev/null || echo "$SRC")"

mkdir -p "$OUT"

echo "== 编译 LiquidUnzip.exe =="
"$CSC" /nologo /platform:anycpu /target:winexe /optimize+ /debug- \
  /out:"$OUT_WIN\\LiquidUnzip.exe" \
  $REFS \
  "$SRC_WIN\\Program.cs" "$SRC_WIN\\Glass.cs" "$SRC_WIN\\Extractor.cs" "$SRC_WIN\\MainWindow.cs"

echo "== 内置 7-Zip =="
cp "C:/Program Files/7-Zip/7z.exe" "$OUT/7z.exe"
cp "C:/Program Files/7-Zip/7z.dll" "$OUT/7z.dll"

echo "== 完成 =="
ls -la "$OUT"
