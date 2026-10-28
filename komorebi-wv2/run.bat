@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist dist\Komorebi\Komorebi.exe (
  start "" dist\Komorebi\Komorebi.exe
  exit /b
)
echo ビルドして起動します（初回のみ少し時間がかかります）...
dotnet run -c Release
