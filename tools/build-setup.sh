#!/bin/bash
# 构建一键安装包：把 exe + 7z 作为嵌入资源打进单个 Setup exe
set -e

CSC="C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
GAC="C:/Windows/Microsoft.NET/assembly"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
W="$(cygpath -w "$ROOT")"

REFS="/r:$GAC/GAC_MSIL/PresentationFramework/v4.0_4.0.0.0__31bf3856ad364e35/PresentationFramework.dll \
/r:$GAC/GAC_64/PresentationCore/v4.0_4.0.0.0__31bf3856ad364e35/PresentationCore.dll \
/r:$GAC/GAC_MSIL/WindowsBase/v4.0_4.0.0.0__31bf3856ad364e35/WindowsBase.dll \
/r:$GAC/GAC_MSIL/System.Xaml/v4.0_4.0.0.0__b77a5c561934e089/System.Xaml.dll"

mkdir -p "$ROOT/release"

echo "== 编译主程序 =="
"$CSC" /nologo /platform:anycpu /target:winexe /optimize+ \
  /out:"$W\\release\\LiquidUnzip.exe" $REFS \
  "$W\\src\\Program.cs" "$W\\src\\Glass.cs" "$W\\src\\Backdrop.cs" "$W\\src\\ArchiveView.cs" \
  "$W\\src\\LiquidSurface.cs" "$W\\src\\Extractor.cs" "$W\\src\\MainWindow.cs"

cp "C:/Program Files/7-Zip/7z.exe" "$ROOT/release/7z.exe"
cp "C:/Program Files/7-Zip/7z.dll" "$ROOT/release/7z.dll"

echo "== 编译安装器（内嵌三个文件） =="
"$CSC" /nologo /platform:anycpu /target:winexe /optimize+ \
  /out:"$W\\release\\LiquidUnzip-Setup-$1.exe" $REFS \
  /resource:"$W\\release\\LiquidUnzip.exe",Payload.LiquidUnzip.exe \
  /resource:"$W\\release\\7z.exe",Payload.7z.exe \
  /resource:"$W\\release\\7z.dll",Payload.7z.dll \
  "$W\\tools\\Setup.cs" "$W\\src\\Glass.cs" "$W\\src\\LiquidSurface.cs" \
  /r:Microsoft.CSharp.dll

echo "== 完成 =="
ls -la "$ROOT/release"/*.exe
