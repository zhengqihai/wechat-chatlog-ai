@echo off
chcp 65001 >nul
title Chatlog AI 首次初始化
cd /d "%~dp0"
if not exist "%~dp0runtime\python\python.exe" (
  echo 缺少便携 Python 运行时，请重新解压完整发行包。
  pause
  exit /b 1
)
set "PYTHONPATH=%~dp0runtime\python-packages"
set "PYTHONUTF8=1"
set "PYTHONNOUSERSITE=1"
set "PYTHONDONTWRITEBYTECODE=1"
"%~dp0runtime\python\python.exe" "%~dp0app\launcher.py" --initialize %1
if errorlevel 1 pause
