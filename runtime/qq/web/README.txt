WeQ Web — 浏览器版

需要 Node.js 22 或更高版本（不内置）。

启动：
  Windows：双击 start.bat
  Linux  ：./start.sh

（依赖已随包预装。若自行删了 node_modules，跑 npm install --omit=dev 补回。）

然后浏览器打开终端里打印的地址，用同时打印的访问令牌登录。

环境变量：
  WEQ_TOKEN       访问令牌。绑定到非本机地址时必须显式设置。
  WEQ_HOST        监听地址，默认 127.0.0.1。设为 0.0.0.0 才对外可见。
  WEQ_PORT        端口，默认 7690；被占用时自动向后探测。
  WEQ_EXPORT_DIR  导出目录，默认 ./weq-exports
  WEQ_DATA_DIR    日志目录，默认 ./weq-data

⚠ 对外暴露前请务必放在 HTTPS 反向代理之后，并设置一个足够长的 WEQ_TOKEN。
   这个服务能读取该机器上 QQ 的全部本地聊天记录。

Linux 建议直接用 root 运行：注入 QQ 进程需要 ptrace 权限，非 root 时会
改走 pkexec 提权，而无图形界面的服务器弹不出授权框。

native/ 同时包含 win32-x64 / linux-x64 / linux-arm64 三份原生模块，
运行时按当前平台自动选择，因此同一个压缩包三平台通用。

完整说明： https://github.com/H3CoF6/WeQ/blob/main/apps/web/README.md