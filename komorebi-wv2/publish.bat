@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 配布用にビルドします（.NET 9 デスクトップランタイムが入っているPC向け・小さい）...
dotnet publish -c Release -r win-x64 --self-contained false -o dist\Komorebi
if errorlevel 1 (
  echo ビルドに失敗しました。
  exit /b 1
)
echo.
echo 完了: dist\Komorebi\Komorebi.exe
echo.
echo ※ .NET が入っていないPCに配る場合は、次を実行してください（約150MB・ランタイム同梱）:
echo    dotnet publish -c Release -r win-x64 --self-contained true -o dist\Komorebi-standalone
