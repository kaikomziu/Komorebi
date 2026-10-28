@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist node_modules (
  echo 初回セットアップ: 依存パッケージをインストールします...
  call npm install
)
call npm start
