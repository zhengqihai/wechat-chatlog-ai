#!/bin/sh
set -e
cd "$(dirname "$0")"

if ! command -v node >/dev/null 2>&1; then
  echo
  echo "  没有找到 Node.js。"
  echo "  请先安装 Node.js 22 或更高版本："
  echo "    Debian/Ubuntu:  curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash - && sudo apt install -y nodejs"
  echo "    其它发行版:      https://nodejs.org/"
  echo
  exit 1
fi

# 主版本号（v22.16.0 -> 22）。
NODE_MAJOR=$(node -p "process.versions.node.split('.')[0]")
if [ "$NODE_MAJOR" -lt 22 ]; then
  echo
  echo "  Node.js 版本过低（当前 v$NODE_MAJOR），需要 22 或更高。"
  echo "  升级方式见 https://nodejs.org/"
  echo
  exit 1
fi

# 注入 QQ 进程要 ptrace 权限。非 root 时会退回 pkexec 提权，而无图形
# 会话的服务器根本弹不出授权框，所以这里提前提示。
if [ "$(id -u)" -ne 0 ]; then
  echo
  echo "  提示：当前不是 root。获取密钥需要注入 QQ 进程（ptrace），"
  echo "        非 root 会改走 pkexec 图形授权，无桌面环境时会失败。"
  echo "        建议改用：sudo ./start.sh"
  echo
fi

if [ ! -d node_modules ]; then
  echo "正在安装依赖..."
  npm install --omit=dev --no-audit --no-fund
fi

exec node server.mjs "$@"
