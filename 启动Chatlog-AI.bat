@echo off
chcp 65001 >nul
title Chatlog AI 实时聊天分析
cd /d "%~dp0"

if not exist "%~dp0runtime\python\python.exe" (
    echo 缺少便携 Python 运行时，请重新解压完整发行包。
    pause
    exit /b 1
)

echo 正在启动 Chatlog AI 服务...
echo 请等待服务就绪后，浏览器访问：http://127.0.0.1:10392

set "PYTHONPATH=%~dp0runtime\python-packages"
set "WECHAT_TOOL_DATA_DIR=D:\app\wechat\Files\xwechat_files"
set "PYTHONUTF8=1"
set "PYTHONNOUSERSITE=1"
set "PYTHONDONTWRITEBYTECODE=1"

"%~dp0runtime\python\python.exe" "%~dp0app\launcher.py"
if errorlevel 1 pause