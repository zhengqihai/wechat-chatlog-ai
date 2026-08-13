# -*- coding: utf-8 -*-
"""统一启动并管理微信、QQ 与 Chatlog AI 三个本地服务。"""

import ctypes
import json
import os
import secrets
import signal
import socket
import subprocess
import sys
import time
import urllib.request
import webbrowser
from ctypes import wintypes
from pathlib import Path


APP_DIR = Path(__file__).resolve().parent
ROOT_DIR = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else APP_DIR.parent
CONFIG_FILE = ROOT_DIR / "config.json"
DATA_DIR = ROOT_DIR / "data"
LOG_DIR = ROOT_DIR / "logs"
RUNTIME_FILE = DATA_DIR / "runtime.json"
SHUTDOWN_FILE = DATA_DIR / ".shutdown-requested"

WEQ_DIR = ROOT_DIR / "runtime" / "qq" / "web"
WEQ_SERVER = WEQ_DIR / "server.mjs"
WEQ_APPDATA_DIR = DATA_DIR / "qq" / "appdata"
WEQ_DATA_DIR = DATA_DIR / "qq" / "weq-data"
WEQ_EXPORT_DIR = DATA_DIR / "qq" / "exports"

WECHAT_RESOURCES = ROOT_DIR / "runtime" / "wechat" / "resources"
WECHAT_BACKEND = WECHAT_RESOURCES / "backend" / "wechat-backend.exe"
WECHAT_NATIVE_DIR = WECHAT_RESOURCES / "backend" / "native"
# WECHAT_DATA_DIR = DATA_DIR / "wechat" / "data"
# WECHAT_OUTPUT_DIR = DATA_DIR / "wechat" / "output"
WECHAT_DATA_DIR = Path(os.environ.get("WECHAT_TOOL_DATA_DIR", DATA_DIR / "wechat" / "data"))
WECHAT_OUTPUT_DIR = Path(os.environ.get("WECHAT_TOOL_OUTPUT_DIR", DATA_DIR / "wechat" / "output"))
CHATLOG_PORT = 18765
WECHAT_PORT = 10392
WEQ_PORT = 7690
CHATLOG_URL = "http://127.0.0.1:%d" % CHATLOG_PORT


class IoCounters(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_uint64),
        ("WriteOperationCount", ctypes.c_uint64),
        ("OtherOperationCount", ctypes.c_uint64),
        ("ReadTransferCount", ctypes.c_uint64),
        ("WriteTransferCount", ctypes.c_uint64),
        ("OtherTransferCount", ctypes.c_uint64),
    ]


class BasicLimitInformation(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_int64),
        ("PerJobUserTimeLimit", ctypes.c_int64),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class ExtendedLimitInformation(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", BasicLimitInformation),
        ("IoInfo", IoCounters),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


class ProcessJob:
    """关闭启动器时终止本启动器创建的服务，不影响微信和 QQ 客户端。"""

    KILL_ON_JOB_CLOSE = 0x00002000
    EXTENDED_LIMIT_INFO_CLASS = 9

    def __init__(self):
        self.handle = None
        if os.name != "nt":
            return
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        handle = kernel32.CreateJobObjectW(None, None)
        if not handle:
            return
        info = ExtendedLimitInformation()
        info.BasicLimitInformation.LimitFlags = self.KILL_ON_JOB_CLOSE
        success = kernel32.SetInformationJobObject(
            handle,
            self.EXTENDED_LIMIT_INFO_CLASS,
            ctypes.byref(info),
            ctypes.sizeof(info),
        )
        if not success:
            kernel32.CloseHandle(handle)
            return
        self.handle = handle

    def assign(self, process):
        if self.handle and os.name == "nt":
            ctypes.WinDLL("kernel32", use_last_error=True).AssignProcessToJobObject(
                self.handle, wintypes.HANDLE(process._handle)
            )

    def close(self):
        if self.handle and os.name == "nt":
            ctypes.WinDLL("kernel32", use_last_error=True).CloseHandle(self.handle)
            self.handle = None


def load_json(path, defaults):
    result = dict(defaults)
    if path.exists():
        try:
            with path.open("r", encoding="utf-8") as file:
                data = json.load(file)
                if isinstance(data, dict):
                    result.update(data)
        except (OSError, ValueError):
            pass
    return result


def save_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as file:
        json.dump(data, file, ensure_ascii=False, indent=2)
        file.write("\n")
    os.replace(str(temporary), str(path))


def load_runtime():
    runtime = load_json(RUNTIME_FILE, {"weq_token": "", "qq_exe": ""})
    if not str(runtime.get("weq_token") or "").strip():
        runtime["weq_token"] = secrets.token_hex(24)
        save_json(RUNTIME_FILE, runtime)
    return runtime


def ensure_directories():
    for path in (
        LOG_DIR,
        WEQ_APPDATA_DIR,
        WEQ_DATA_DIR,
        WEQ_EXPORT_DIR,
        WECHAT_DATA_DIR,
        WECHAT_OUTPUT_DIR,
    ):
        path.mkdir(parents=True, exist_ok=True)


def require_paths():
    required = [WEQ_SERVER, WECHAT_BACKEND, WECHAT_RESOURCES / "ui", CONFIG_FILE]
    missing = [str(path) for path in required if not path.exists()]
    if missing:
        raise RuntimeError("缺少运行文件：\n" + "\n".join(missing))


def resolve_node():
    bundled = ROOT_DIR / "runtime" / "node" / "node.exe"
    node = str(bundled) if bundled.exists() else ""
    if not node:
        raise RuntimeError("便携 Node.js 不存在：%s" % bundled)
    major = int(subprocess.check_output(
        [node, "-p", "process.versions.node.split('.')[0]"],
        text=True,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    ).strip())
    if major < 22:
        raise RuntimeError("随包 Node.js 版本过低，当前为 %d，需要 22 或更高版本" % major)
    return node


def ensure_port_free(port):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(0.3)
        if sock.connect_ex(("127.0.0.1", port)) == 0:
            raise RuntimeError("端口 %d 已被占用，请先关闭旧的 Chatlog AI 相关服务" % port)


def wait_http(url, timeout, expected_status=None):
    deadline = time.time() + timeout
    last_error = None
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as response:
                if expected_status is None or response.status == expected_status:
                    return
        except Exception as exc:
            last_error = exc
        time.sleep(0.4)
    raise RuntimeError("服务启动超时：%s（%s）" % (url, last_error or "无响应"))


def wait_port(port, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.settimeout(0.5)
            if sock.connect_ex(("127.0.0.1", port)) == 0:
                return
        time.sleep(0.3)
    raise RuntimeError("服务端口启动超时：%d" % port)


def start_process(command, cwd, env, log_name, job):
    log_path = LOG_DIR / log_name
    log_file = log_path.open("a", encoding="utf-8", buffering=1)
    log_file.write("\n[%s] 启动：%s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), " ".join(command)))
    process = subprocess.Popen(
        command,
        cwd=str(cwd),
        env=env,
        stdin=subprocess.DEVNULL,
        stdout=log_file,
        stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    job.assign(process)
    return process, log_file


def stop_process_tree(process):
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW,
            check=False,
        )
    else:
        process.terminate()
    try:
        process.wait(timeout=8)
    except subprocess.TimeoutExpired:
        process.kill()


def install_signal_handlers(stop_flag):
    def request_stop(_signum, _frame):
        stop_flag[0] = True

    for name in ("SIGINT", "SIGTERM", "SIGBREAK"):
        signum = getattr(signal, name, None)
        if signum is not None:
            try:
                signal.signal(signum, request_stop)
            except (OSError, ValueError):
                pass


def build_wechat_env():
    environment = os.environ.copy()
    environment.update({
        "WECHAT_TOOL_HOST": "127.0.0.1",
        "WECHAT_TOOL_PORT": str(WECHAT_PORT),
        "WECHAT_TOOL_DATA_DIR": str(WECHAT_DATA_DIR),
        "WECHAT_TOOL_OUTPUT_DIR": str(WECHAT_OUTPUT_DIR),
        "WECHAT_TOOL_UI_DIR": str(WECHAT_RESOURCES / "ui"),
        "WECHAT_TOOL_DESKTOP_PARENT_PID": str(os.getpid()),
        "WECHAT_TOOL_NATIVE_CORE_MODE": "required",
        "WECHAT_TOOL_NATIVE_CORE_LIBRARY": str(WECHAT_NATIVE_DIR / "wechatdb_client.dll"),
        "WECHAT_TOOL_NATIVE_CORE_BROKER": str(WECHAT_NATIVE_DIR / "wechatdb_broker.exe"),
        "WCE_INTEGRITY_NATIVE_PATH": str(WECHAT_NATIVE_DIR / "wce_integrity.pyd"),
        "PYTHONIOENCODING": "utf-8",
    })
    return environment


def build_weq_env(runtime):
    environment = os.environ.copy()
    environment.update({
        "APPDATA": str(WEQ_APPDATA_DIR),
        "WEQ_HOST": "127.0.0.1",
        "WEQ_PORT": str(WEQ_PORT),
        "WEQ_TOKEN": str(runtime["weq_token"]),
        "WEQ_DATA_DIR": str(WEQ_DATA_DIR),
        "WEQ_EXPORT_DIR": str(WEQ_EXPORT_DIR),
    })
    qq_exe = str(runtime.get("qq_exe") or "").strip()
    if qq_exe:
        environment["WEQ_QQ_EXE"] = qq_exe
    return environment


def start_data_services(node, runtime, job, processes, logs, start_wechat=True, start_qq=True):
    if start_wechat:
 # ----- 新增：预先启动 native core broker -----
        import subprocess
        import time
        broker_path = WECHAT_NATIVE_DIR / "wechatdb_broker.exe"
        if broker_path.exists():
            # 以后台无窗口方式启动 broker，并传入正确的数据库路径
            broker_cmd = [
                str(broker_path),
                "--database-root", str(WECHAT_DATA_DIR),
                "--parent-pid", str(os.getpid())
            ]
            subprocess.Popen(
                broker_cmd,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW  # Windows 下隐藏窗口
            )
            # 等待短暂时间让 broker 初始化
            time.sleep(1)
        # ----- 新增结束 -----

        print("正在启动微信实时数据服务……")
        process, log_file = start_process(
            [str(WECHAT_BACKEND)], WECHAT_BACKEND.parent, build_wechat_env(), "wechat.log", job
        )
        processes.append(process)
        logs.append(log_file)
        wait_http("http://127.0.0.1:%d/api/health" % WECHAT_PORT, 180, 200)

    if start_qq:
        print("正在启动 QQ 实时数据服务……")
        process, log_file = start_process(
            [node, str(WEQ_SERVER)], WEQ_DIR, build_weq_env(runtime), "qq.log", job
        )
        processes.append(process)
        logs.append(log_file)
        wait_port(WEQ_PORT, 60)


def has_wechat_key():
    key_file = WECHAT_OUTPUT_DIR / "account_keys.json"
    if not key_file.exists():
        return False
    try:
        data = json.loads(key_file.read_text(encoding="utf-8"))
        return isinstance(data, dict) and any(
            isinstance(item, dict) and str(item.get("db_key") or "").strip()
            for item in data.values()
        )
    except (OSError, ValueError):
        return False


def has_qq_key():
    account_dir = WEQ_APPDATA_DIR / "weq" / "config" / "accounts"
    for path in account_dir.glob("*.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            if str(data.get("dbKey") or "").strip():
                return True
        except (OSError, ValueError):
            continue
    return False


def open_initialization_pages(runtime, status=None):
    wechat_ready, qq_ready = status or (has_wechat_key(), has_qq_key())
    print("\n首次初始化状态：")
    print("  微信：%s" % ("已完成，本次跳过" if wechat_ready else "未完成，将打开授权页面"))
    print("  QQ：%s" % ("已完成，本次跳过" if qq_ready else "未完成，将打开授权页面"))
    if not wechat_ready:
        webbrowser.open("http://127.0.0.1:%d" % WECHAT_PORT)
    if not qq_ready:
        print("  QQ 页面访问令牌：%s" % runtime["weq_token"])
        webbrowser.open("http://127.0.0.1:%d" % WEQ_PORT)
    if wechat_ready and qq_ready:
        print("两个平台都已完成本机授权，无需重复操作。")
    else:
        print("请只完成上面标记为“未完成”的平台。成功后先关闭本窗口，")
        print("再双击“启动Chatlog-AI.bat”进入统一聊天页面。")
    return wechat_ready, qq_ready


def report_initialization_progress(previous_status):
    """检测授权文件变化，只在状态变化时输出明确的下一步。"""
    current_status = (has_wechat_key(), has_qq_key())
    names = ("微信", "QQ")
    for index, ready in enumerate(current_status):
        if ready and not previous_status[index]:
            print("\n%s 授权已保存，后续启动将自动读取该平台。" % names[index])
    if current_status != previous_status and all(current_status):
        print("\n微信和 QQ 授权均已完成。")
        print("下一步：关闭本初始化窗口，再双击“启动Chatlog-AI.bat”。")
    return current_status


def server_command():
    if getattr(sys, "frozen", False):
        return [sys.executable, "--server"]
    return [sys.executable, str(APP_DIR / "server.py")]


def run(initialization=False):
    processes = []
    logs = []
    job = ProcessJob()
    stop_flag = [False]
    install_signal_handlers(stop_flag)

    try:
        print("正在检查 Chatlog AI 运行环境……")
        ensure_directories()
        if SHUTDOWN_FILE.exists():
            SHUTDOWN_FILE.unlink()
        require_paths()
        runtime = load_runtime()
        initialization_status = (has_wechat_key(), has_qq_key()) if initialization else None
        start_wechat = not initialization or not initialization_status[0]
        start_qq = not initialization or not initialization_status[1]
        node = resolve_node() if start_qq else ""
        ports = []
        if start_wechat:
            ports.append(WECHAT_PORT)
        if start_qq:
            ports.append(WEQ_PORT)
        if not initialization:
            ports.append(CHATLOG_PORT)
        for port in ports:
            ensure_port_free(port)
        start_data_services(node, runtime, job, processes, logs, start_wechat, start_qq)

        if initialization:
            print("\n首次初始化服务已启动。")
            initialization_status = open_initialization_pages(runtime, initialization_status)
            if all(initialization_status):
                print("本窗口将在 3 秒后自动关闭。以后直接双击“启动Chatlog-AI.bat”。")
                time.sleep(3)
                stop_flag[0] = True
            else:
                print("\n此窗口关闭前请勿关闭正在授权的微信或 QQ 客户端。")
        else:
            print("正在启动统一分析服务……")
            environment = os.environ.copy()
            environment["CHATLOG_ROOT_DIR"] = str(ROOT_DIR)
            environment["CHATLOG_SHUTDOWN_FILE"] = str(SHUTDOWN_FILE)
            process, log_file = start_process(
                server_command(), ROOT_DIR, environment, "chatlog.log", job
            )
            processes.append(process)
            logs.append(log_file)
            wait_http(CHATLOG_URL + "/api/health", 40, 200)
            print("已启动：%s" % CHATLOG_URL)
            print("关闭此窗口、按 Ctrl+C 或在网页点退出，三个本地服务都会一起结束。")
            if os.environ.get("CHATLOG_NO_BROWSER") != "1":
                webbrowser.open(CHATLOG_URL)

        while not stop_flag[0]:
            if not initialization and SHUTDOWN_FILE.exists():
                print("收到网页退出请求，正在关闭全部本地服务……")
                stop_flag[0] = True
                continue
            for index, child in enumerate(processes):
                if child.poll() is not None:
                    raise RuntimeError("本地服务异常退出，编号：%d" % (index + 1))
            if initialization and initialization_status is not None:
                initialization_status = report_initialization_progress(initialization_status)
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    except Exception as exc:
        print("启动失败：%s" % exc)
        print("日志目录：%s" % LOG_DIR)
        if os.environ.get("CHATLOG_NO_PAUSE") != "1":
            try:
                input("按回车键退出……")
            except (EOFError, KeyboardInterrupt):
                pass
    finally:
        print("正在关闭 Chatlog AI 本地服务……")
        for process in reversed(processes):
            stop_process_tree(process)
        job.close()
        for log_file in logs:
            try:
                log_file.close()
            except Exception:
                pass
        try:
            if SHUTDOWN_FILE.exists():
                SHUTDOWN_FILE.unlink()
        except OSError:
            pass


def main():
    if "--server" in sys.argv:
        # 冻结为单文件程序后，统一后端由同一可执行文件的子进程承载。
        os.environ.setdefault("CHATLOG_ROOT_DIR", str(ROOT_DIR))
        from server import main as server_main
        server_main()
        return
    run(initialization="--initialize" in sys.argv)


if __name__ == "__main__":
    main()
