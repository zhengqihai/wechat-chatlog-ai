@echo off
setlocal
cd /d "%~dp0"

where node >nul 2>nul
if errorlevel 1 (
  echo.
  echo   没有找到 Node.js。
  echo   请先安装 Node.js 22 或更高版本： https://nodejs.org/
  echo.
  pause
  exit /b 1
)

rem 取主版本号（v22.16.0 -> 22）并要求 >= 22。
for /f "tokens=1 delims=." %%v in ('node -p "process.versions.node"') do set NODE_MAJOR=%%v
if %NODE_MAJOR% LSS 22 (
  echo.
  echo   Node.js 版本过低（当前 v%NODE_MAJOR%），需要 22 或更高。
  echo   请到 https://nodejs.org/ 升级。
  echo.
  pause
  exit /b 1
)

if not exist "node_modules" (
  echo 正在安装依赖...
  call npm install --omit=dev --no-audit --no-fund || exit /b 1
)

node server.mjs %*
pause
