# Chatlog AI 桌面客户端（.NET / WPF）改造方案

> 用途：本文件是「把网页客户端改造成原生 .NET 桌面程序」这一改造的**单一事实源**。
> 新开对话窗口时，先读 `docs/PROJECT_SCAN.md`（改造前的现状）再读本文件（改造方案与进度），即可免去重新描述需求。
>
> 建立时间：2026-09-10
> 改造范围：仅替换「启动器 + 前端界面」；Python 后端（`server.py` / `adapters.py` / `automation.py`）保持。

---

## 1. 背景与目标

### 1.1 现状（改造前）

用户拿到的是一个**便携目录 + 批处理**，双击后有三个"露出网页/控制台"的地方：

| # | 现象 | 位置 |
|---|---|---|
| 1 | 控制台黑窗常驻（`print` 输出） | `app/launcher.py` |
| 2 | 授权阶段弹出**浏览器**访问 `http://127.0.0.1:10392` | [launcher.py L423](file:///d:/code/chatlog-ai-portable/app/launcher.py#L423) |
| 3 | 主界面弹出**浏览器**访问 `http://127.0.0.1:18765` | [launcher.py L511](file:///d:/code/chatlog-ai-portable/app/launcher.py#L511) |

### 1.2 目标

1. 用户安装后**只有一个原生桌面程序**，双击桌面图标即可启动，**没有控制台黑窗、没有浏览器地址栏**。
2. **唯一保留浏览器的一步**是微信数据源授权（`http://127.0.0.1:10392`）——该页面由上游 `wechat-backend.exe` 提供，无法原生化。
3. 安装时/安装后询问用户**是否创建桌面快捷方式**。
4. 新增**导出聊天记录**功能（仅当前会话）。

### 1.3 非目标（本次明确不做）

- 不用 C# 重写 `server.py` / `adapters.py` / `automation.py`。Python 继续承担全部后端职责。
- 不改上游 `runtime/wechat/` 内的任何文件。

---

## 2. 已确认决策（用户已拍板，勿再询问）

| # | 决策项 | 结论 |
|---|---|---|
| D1 | .NET 客户端职责 | **全接管**「启动器 + 界面」。Python 只保留 `server.py` 当后台；`.bat` 与 `launcher.py` 退役 |
| D2 | 界面技术 | **WPF 原生控件重写**（不用 WebView2） |
| D3 | 安装与分发 | **Inno Setup** + 内置便携 Python（`runtime/` 原样打包） |
| D4 | 提权策略 | **启动时直接申请管理员**（manifest `requireAdministrator`），接受每次弹 UAC |
| D5 | 安装目录 | **程序装 Program Files（只读），可写数据放 ProgramData** |
| D6 | 导出格式 | **Markdown / HTML / JSON / CSV**（不做 TXT） |
| D7 | 导出范围 | **仅当前会话**；带**时间范围筛选**；**可选含 AI 分析历史** |
| D8 | 推进方式 | 直接开工，边做边更新本文件 |
| D9 | 图标 | 先生成占位图标；替换方法见 §9 |

---

## 3. 已验证事实（施工前提，均为实测）

### 3.1 开发环境

| 项 | 实测值 |
|---|---|
| .NET SDK | `8.0.425`（位于 `C:\Program Files\dotnet\sdk`） |
| 桌面运行时 | `Microsoft.WindowsDesktop.App 8.0.31` → **WPF 可用** |
| **注意** | `dotnet` **不在终端 PATH 里**，脚本中须用全路径 `C:\Program Files\dotnet\dotnet.exe` |

### 3.2 便携 Python 的启动契约（★ 必须逐字复刻）

来源：[启动Chatlog-AI.bat](file:///d:/code/chatlog-ai-portable/启动Chatlog-AI.bat#L16-L21)

```
PYTHONPATH            = <安装目录>\runtime\python-packages
PYTHONUTF8            = 1
PYTHONNOUSERSITE      = 1
PYTHONDONTWRITEBYTECODE = 1
解释器                = <安装目录>\runtime\python\python.exe
入口                  = <安装目录>\app\launcher.py  [--initialize]
```

实测：`runtime/python/python.exe` 与 `runtime/python/pythonw.exe` 均存在；依赖包在 `runtime/python-packages`（无 `.pth`、无 `Lib/site-packages`），**完全依赖 `PYTHONPATH`**，漏设即 `import fastapi` 失败。

### 3.3 关键路径（实测存在）

| 用途 | 相对安装目录的路径 |
|---|---|
| 微信后端 | `runtime\wechat\resources\backend\wechat-backend.exe` |
| 原生库 broker | `runtime\wechat\resources\backend\native\wechatdb_broker.exe` |
| 原生库目录 | `runtime\wechat\resources\backend\native\`（含 `wechatdb_client.dll`、`wce_integrity.pyd`） |
| 微信后端 UI | `runtime\wechat\resources\ui` |
| 内置 ffmpeg | `runtime\wechat\resources\ffmpeg\ffmpeg.exe` |
| 前端静态资源 | `app\static`（`server.py` 通过 `STATIC_DIR` 读取） |

### 3.4 ★ 路径耦合（本次改造必须解决的核心问题）

[server.py L31-L41](file:///d:/code/chatlog-ai-portable/app/server.py#L31-L41)：

```python
ROOT_DIR   = CHATLOG_ROOT_DIR            # 只能有一个
CONFIG_DIR = ROOT_DIR / "config"         # 需要可写
DATA_DIR   = ROOT_DIR / "data"           # 需要可写
STATIC_DIR = ROOT_DIR / "app" / "static" # 只读
```

「只读的程序文件」与「可写的用户数据」被绑在同一个 `CHATLOG_ROOT_DIR` 上。
装成正式程序后程序文件在 `Program Files`（不可写），因此**必须新增一个独立的数据根目录**，见 §11。

### 3.5 端口与环境变量清单

| 端口 | 用途 |
|---|---|
| `18765` | 本项目 FastAPI 后端 |
| `10392` | 微信数据源后端（授权页也在这个端口） |

`launcher.py` 注入给子进程的环境变量（.NET 必须等价实现）：

| 变量 | 值 |
|---|---|
| `WECHAT_TOOL_HOST` | `127.0.0.1` |
| `WECHAT_TOOL_PORT` | `10392` |
| `WECHAT_TOOL_DATA_DIR` | 微信数据库根目录（→ ProgramData） |
| `WECHAT_TOOL_OUTPUT_DIR` | 密钥/导出输出目录（→ ProgramData） |
| `WECHAT_TOOL_UI_DIR` | `<安装目录>\runtime\wechat\resources\ui` |
| `WECHAT_TOOL_DESKTOP_PARENT_PID` | 父进程 PID（→ .NET 进程 PID） |
| `WECHAT_TOOL_NATIVE_CORE_MODE` | `required` |
| `WECHAT_TOOL_NATIVE_CORE_LIBRARY` | `<native>\wechatdb_client.dll` |
| `WECHAT_TOOL_NATIVE_CORE_BROKER` | `<native>\wechatdb_broker.exe` |
| `WCE_INTEGRITY_NATIVE_PATH` | `<native>\wce_integrity.pyd` |
| `WECHAT_TOOL_FFMPEG` | `<...>\ffmpeg\ffmpeg.exe`（存在时才设） |
| `PYTHONIOENCODING` | `utf-8` |

`server.py` 需要的两个（由 .NET 注入）：

| 变量 | 值 |
|---|---|
| `CHATLOG_ROOT_DIR` | 安装目录（供 `STATIC_DIR` 定位） |
| `CHATLOG_DATA_DIR` | ProgramData 数据目录（**新增**，见 §11） |
| `CHATLOG_SHUTDOWN_FILE` | `<数据目录>\data\.shutdown-requested` |

---

## 4. 目标架构

```
用户双击桌面「Chatlog AI」
  │  (ChatlogAI.exe，WPF，app.manifest: requireAdministrator, DPI aware)
  ├─ 阶段0  环境自检   ：建目录、补齐 config/、校验必需文件
  ├─ 阶段1  启动微信源 ：wechatdb_broker.exe → wechat-backend.exe，绑 Job Object
  │                     轮询 http://127.0.0.1:10392/api/health（上限 180s）
  ├─ 阶段2  授权判定
  │         ├─ 未授权 → 打开默认浏览器 10392 + 客户端显示「等待授权」遮罩
  │         │            轮询账号密钥文件出现非空 db_key
  │         └─ 已授权 → 跳过
  ├─ 阶段3  账号选择   ：多账号时弹原生对话框（替代原 input()）
  ├─ 阶段4  启动后端   ：python.exe app\server.py（注入 §3.2/§3.5 环境变量），绑同一 Job Object
  │                     轮询 http://127.0.0.1:18765/api/health（上限 40s）
  └─ 阶段5  进入主界面 ：原生 WPF，全部数据走 REST
退出：关闭 Job Object → 微信后端 + Python 后端 整组结束（等价原 taskkill 效果）
```

**生命周期关键点**：原方案用 Windows Job Object（`KILL_ON_JOB_CLOSE`）实现"关掉启动器 → 两个服务一起死"（[launcher.py L86-L123](file:///d:/code/chatlog-ai-portable/app/launcher.py#L86-L123)）。.NET 必须用 P/Invoke 重写同款能力，**否则会出现残留进程占端口**。

> 已知缺陷一并继承：[launcher.py L291-L300](file:///d:/code/chatlog-ai-portable/app/launcher.py#L291-L300) 的 `wechatdb_broker.exe` **未纳入 Job Object**，只靠自身 `--parent-pid` 自杀兜底。.NET 版应考虑把它也 `AssignProcessToJobObject`（需评估 `CREATE_SUSPENDED` 或分 Job 处理）。

---

## 5. 路径模型

```
[只读] 安装目录  %ProgramFiles%\ChatlogAI\
        ├─ ChatlogAI.exe              ← .NET 客户端
        ├─ app\server.py ...          ← Python 后端源码
        ├─ app\static\ ...            ← 前端静态资源（保留，后端仍会 mount）
        └─ runtime\ ...               ← 便携 Python + 微信后端 + ffmpeg

[可写] 数据目录  %ProgramData%\ChatlogAI\
        ├─ config\  config.json / api-keys.json / prompts\ / auto-reply\
        ├─ data\    summaries.json / wechat\{data,output}\ / .shutdown-requested
        └─ logs\    chatlog.log / wechat.log
```

**为什么不做 `%LOCALAPPDATA%`**：程序需要以管理员身份运行。若当前是标准账户、提权时输入的是**另一个管理员账号**，`%LOCALAPPDATA%` 会解析到那个账号的家目录，导致配置"丢失"。`ProgramData` 是机器级、提权后稳定可写，是 Windows 对"机器级应用的共享数据"的标准位置。

---

## 6. 新增目录结构

```
chatlog-ai-portable/
├─ client/                                  ★ 新增
│  ├─ ChatlogAI.sln
│  ├─ ChatlogAI.Client/                     ← 主程序（启动器 + UI）
│  │  ├─ ChatlogAI.Client.csproj
│  │  ├─ app.manifest                       ← requireAdministrator / DPI aware
│  │  ├─ Assets/app.ico                     ← 占位图标（替换方法见 §9）
│  │  ├─ App.xaml / App.xaml.cs
│  │  ├─ Bootstrap/                         ← 对应原 launcher.py
│  │  │  ├─ AppPaths.cs                     路径解析（§5 模型）
│  │  │  ├─ ConfigBootstrapper.cs           等价 ensure_directories/ensure_config_files
│  │  │  ├─ PortGuard.cs                    等价 ensure_port_free
│  │  │  ├─ JobObjectHost.cs                P/Invoke Job Object
│  │  │  ├─ ChildProcess.cs                 子进程启动 + 日志重定向
│  │  │  ├─ WechatAuthWatcher.cs            授权轮询
│  │  │  └─ StartupOrchestrator.cs          两阶段状态机
│  │  ├─ Services/ChatlogApiClient.cs       REST 客户端
│  │  ├─ Models/                            数据模型（§API_REFERENCE §8）
│  │  ├─ Views/                             StartupWindow / MainWindow / 各弹窗
│  │  └─ ViewModels/
│  └─ installer/ChatlogAI.iss               Inno Setup 脚本
```

---

## 7. 任务清单与进度

> 状态：`[ ]` 未开始　`[~]` 进行中　`[x]` 已完成

| # | 任务 | 状态 | 备注 |
|---|---|---|---|
| T1 | 撰写本方案文档 | `[x]` | 文件：`docs/DOTNET_CLIENT_PLAN.md` |
| T2 | `server.py` 新增 `CHATLOG_DATA_DIR`，实现程序/数据目录分离 | `[x]` | 向后兼容，便携模式行为不变；详见 §11 |
| T3 | 搭建 `client/` 解决方案骨架（csproj + 提权 manifest + 占位图标） | `[x]` | `ChatlogAI.sln` / `ChatlogAI.Client.csproj` / `app.manifest` / `Assets/app.ico` |
| T4 | Bootstrap 层：路径 / 配置补齐 / 端口守卫 / Job Object / 子进程 | `[x]` | 已建 6 个文件，见 §7.1；`StartupOrchestrator.cs` 归入 T5 |
| T5 | 启动编排 + 授权等待（阶段 0-5）+ 启动进度窗口 | `[x]` | 已编译通过（0 error / 0 warning）；文件清单见 §7.2；broker 已纳入 Job Object（补 R2）；**真机验证通过，记录见 §7.4** |
| T6 | 编译验证生成可执行文件 | `[x]` | `client\ChatlogAI.Client\bin\Debug\net8.0-windows\ChatlogAI.exe` |
| T7 | REST 客户端 `ChatlogApiClient` + 数据模型 | `[x]` | 已编译通过；`Services/` 2 文件 + `Models/` 4 文件，见 §7.3 |
| T8 | 主界面：会话列表 / 消息区 / AI 分析区（含 Markdown 渲染） | `[x]` | 已编译通过（0 error / 0 warning）；文件清单与六条设计决定见 §7.5；原 `app.js` 59 个函数的主链路已接通 |
| T9 | 模型设置弹窗（供应商增删改/验证/切换/余额） | `[x]` | 已编译通过（0 error / 0 warning）；文件清单、三条相对原前端的"补漏"与四处分歧决定见 §7.9 |
| T10 | 自动回复弹窗（守护对象/提示词/快捷键测试/状态轮询） | `[x]` | 已编译通过（0 error / 0 warning）；文件清单、设计决定与三处刻意偏离见 §7.8 |
| T11 | 导出聊天记录（Markdown/HTML/JSON/CSV，当前会话） | `[x]` | 规格见 §8；文件清单、七条设计决定与唯一一处共享文件改动见 §7.7 |
| T12 | Inno Setup 安装包（询问桌面快捷方式、注册卸载） | `[ ]` | |
| T13 | 旧数据迁移（便携版 `config/`、`data/` → ProgramData） | `[ ]` | 仅首次安装时 |
| T14 | 退役 `launcher.py` 与两个 `.bat` | `[x]` | **2026-09-11 提前完成，见 §7.18**（连同网页版 `app/static/` 一起删除）。`--initialize` 能力由安装包的「Chatlog AI 重新初始化」快捷方式承接 |
| T15 | UI 主题改造：抽公共主题资源字典，深色 → 浅色 + 微信绿 | `[x]` | 已编译通过（0 error / 0 warning）；文件清单与四个 WPF 踩坑记录见 §7.6 |

**第二批需求（2026-09-11 提出，规划见 §14）**

| # | 任务 | 状态 | 备注 |
|---|---|---|---|
| T16 | 主界面导航重构：左侧图标栏（rail）+ 模块化视图，现四栏收敛为「聊天模块」 | `[x]` | 已完成（`dotnet build` 0 error / 0 warning）；文件清单、四条必须遵守的约定与遗留见 §7.10 |
| T17 | 启动免责声明窗口 | `[x]` | 已完成；交付记录见 §7.14（含"原文只改错字"的处理口径） |
| T18 | 主题体系拆两套（浅色 / 深色）+ 切换入口 | `[x]` | 已完成；拆法与三个"极性冲突"见 §7.14，**遗留缺口见 T24** |
| T19 | 微信账号切换（解析新账号 / 切换生效 / 唤起窗口 + 一致性提醒） | `[x]` | 契约与规格见 **§7.17**；T19-A（后端）与 T19-B（客户端）均已完成。**真机已验（单账号，5 项全过）**：启动自动弹窗并停在待确认区、头像警示标记、设置里「自动发送：已暂停」、**闸门确实拦住了发送**、确认后状态清除。**仍未验证**：真的切到第二个微信账号（读的是新账号数据、消息发给新账号里的人）——无第二个已授权账号，只能等第三方反馈，详见 §7.17 的真机验证记录 |
| T20 | 快捷键：手工配置 + 一键恢复默认值 + 探测骨架（原「自动识别」） | `[x]` | 已完成；交付记录见 §7.19（含三层旧配置兼容、两层提示文案，以及"5 个键在微信里是否真生效只能真机验"这条限制） |
| T21 | 后端代理接口：通讯录 + 朋友圈 | `[x]` | 已完成（`dotnet build` 0/0 + `import server` 通过）；10 条新端点与给 T22/T23 的三条关键信息见 §7.11 |
| T22 | 联系人模块（列表多选 / 全选 / 一键导出） | `[x]` | 已完成（构建 0/0 + 真机 6 项点检全过）；交付记录与遗留见 §7.12 |
| T23 | 朋友圈模块（时间线查看 + 媒体 + 导出，**不做 AI 总结**） | `[x]` | 已完成（构建 0/0 + 真机 8 项点检全过）；交付记录、设计约定与遗留见 §7.13 |
| T24 | 深色主题对比度缺口：新增 `AccentTextBrush` 并改 5 个用点 | `[x]` | 已完成，见 §7.14 里的「✅ T24 已修复」（含实测对比度、5 个用点清单，以及"`DynamicResource` 静默退化、构建通过不算证据"这条验证要点） |
| T25 | 后端 bug 修复：上游 keep-alive 重试 + 系统消息不触发自动回复 + 错误文案遗留 | `[x]` | 已完成。重试 `total=2/connect=2/read=0/status=0`、系统消息排除、签名先更新再判冷却、引擎侧二次夹取，均已核对 |
| T26 | 回复间隔（同一会话的最小回复间隔）可配置 | `[x]` | 契约与分工见 §7.16。客户端 + 后端两半均已落地 |

### 7.19 T20 交付记录（快捷键，已完成）

**状态**：`dotnet build` 0 error / 0 warning；Python 导入自检通过，`/api/` 路由 **41 → 42**。实测后端默认值为
`{open_wechat: ctrl+alt+w, search: ctrl+f, paste: ctrl+v, send: enter, confirm: enter}`；`HOTKEY_DETECTORS = []`。

**本任务没有做"自动识别"**（§14.3.0 已取证证伪：微信 4.1 的快捷键读不到——注册表是空键、账号级配置是加密二进制），只做了四件事：

| # | 交付内容 | 落点 |
|---|---|---|
| 1 | 第 5 个可配键 `confirm`（"搜索后确认进入会话"，默认 `enter`） | `app/server.py` 的 `AUTO_REPLY_DEFAULTS["hotkeys"]` |
| 2 | 把写死的 `enter` 改为可配 | `app/automation.py` 的 `send_wechat_message`：`confirm_hk = str(hotkeys.get("confirm") or "enter")` → `press_hotkey(confirm_hk)` |
| 3 | 探测骨架端点（**恒 `found:false`**，探测源列表为空） | `POST /api/auto-reply/detect-hotkeys` + `HOTKEY_DETECTORS = []` |
| 4 | 客户端第 5 行 + 「自动识别」/「恢复默认值」+ 两层提示 | `AutoReplyModels` / `AutoReplyViewModel` / `AutoReplyWindow.xaml` / `ChatlogApiClient` |

**旧配置兼容 = 三层兜底**（升级后行为与升级前完全一致，这是本任务最要紧的一条）：

1. **后端合并**：`load_auto_reply()` 把 `AUTO_REPLY_DEFAULTS["hotkeys"]` 与用户存的 hotkeys 合并 → 老配置自动获得 `confirm="enter"`；
2. **自动化兜底**：`automation.py` 里的 `or "enter"`——即使调用方传进一个不含 `confirm` 的字典（其它调用方、`/api/auto-reply/test` 的 `full` 动作），行为也与写死 `enter` 时一致；
3. **客户端回填**：`AutoReplyViewModel.ApplyConfig` 走既有的 `Fallback(hotkeys?.Confirm, DefaultConfirmHotkey)`，且 `DefaultConfirmHotkey = "enter"`。

**三处设计细节（后续别改回去）**：

- **提示文案分两层**：常驻是「这些值需要你在微信「设置 → 快捷键」里对照填写；程序不会自动读取」，**点「自动识别」之后**才换成「未能自动识别（微信 4.1 未提供可读的快捷键设置），请手工填写……」。**不能一进界面就写"识别失败"**——用户还没点任何按钮，会以为程序刚试过并失败了。（§14.3.6 风险 4：文案必须含"请手工填写微信设置里的值"这个动作指引，只写"识别失败"用户会一直以为自己用错了。）
- **「恢复默认值」只填表单、不落盘**：与这个表单"编辑后保存"的既有语义一致。
- **`found:false` 是正常结果** → 只更新提示区、**不弹框**；但接口本身失败（503 / 网络异常）走既有错误呈现。两者不能混成同一句"未能自动识别"，否则会把真故障藏起来。

**自测时明确禁掉的**：调 `POST /api/auto-reply/test` 或点界面上的「测试」按钮——那是"盲发按键"（`send` 命中就是真发一条微信消息），§14.3.3 已把"试按键式探测"整体排除。因此**「5 个键在微信里是否真的生效」只能由用户真机验证**（先把微信切到前台再点「测试」）。

**仍然存在的限制**：探测接口恒 `found:false`（设计如此，不是缺陷）；「自动识别」不填表单（§14.3.2：识别结果不直接生效——识别错了最坏是"填了个错的默认值"，用户看得见、能改；若直接落盘，用户会以为程序"知道"他的设置，错了也不会去查）。

### 7.18 网页版前端与 Python 启动器退役（T14 提前完成，2026-09-11）

**背景**：用户确认"网页端彻底放弃"，并要求在打包前清理干净。这同时把原计划的 T14（退役 `launcher.py` 与两个 `.bat`）一并做了。

**删除清单（6 个文件 + 1 个目录）**

| 删除对象 | 说明 |
|---|---|
| `app/static/index.html` / `style.css` / `app.js` | 网页版前端本体（3 个文件，63 KB） |
| `app/static/` | 目录随之清空删除 |
| `app/launcher.py` | Python 启动器（职责已由 `Bootstrap/StartupOrchestrator.cs` 等接管） |
| `启动Chatlog-AI.bat` / `首次初始化.bat` | 两个入口脚本，均指向已删的 `launcher.py` |

**可恢复性**：`app.js` 与 `style.css` **此前未被 git 跟踪**，删除前专门提交了一次（commit `03cf2f5`）。取回方式：`git show 03cf2f5:app/static/app.js`。`launcher.py` 与两个 `.bat` 本来就在 git 里，`git log` 可查。

**连带改的代码（不改会坏）**

| 位置 | 改动 |
|---|---|
| `server.py` 顶部 import | 删 `from fastapi.staticfiles import StaticFiles`；`FileResponse` 从 `fastapi.responses` 的 import 去掉（全文件只那一处用过） |
| `server.py` `STATIC_DIR` | 删除定义 |
| `server.py` 挂载块 | 删 `if STATIC_DIR.exists(): app.mount("/static", ...)`。**`StaticFiles(directory=...)` 对不存在的目录会抛异常，所以这两处必须同批删除，否则启动即崩** |
| `server.py` `@app.get("/")` | **保留**，去掉 `index.html` 分支，只返回 `{"message": "Chatlog AI 后端服务正常运行。"}`（"服务在跑吗"的探针） |
| `launcher.py` | 随文件删除；其"自动打开浏览器到 18765"的行为随之消失 |
| `config_defaults.py` 文档串 | 去掉对 launcher 的引用 |

**已查证、确认不受影响的**

- `client/**` 里 4 处提到 `app/static` **全是溯源注释**（"对照原前端 app.js"），无功能依赖。
- 打包：`build-installer.ps1` 的必需文件清单与 `ChatlogAI.iss` **都不含** `app/static`。
- 客户端启动自检 `ConfigBootstrapper.RequirePaths()` 只要求 5 项（`WechatBackendExe` / `WechatUiDir` / `PythonExe` / `ServerScript` / `ConfigFile`），**不含** static → 删了不会拒绝启动。
- **`runtime/wechat/resources/ui/**` 是上游数据源的界面**，`RequirePaths` 与打包脚本都要求它存在 → **绝不可删**。
- `--initialize`（换账号 / 重扫密钥）能力**不丢**：安装包已装了开始菜单快捷方式「Chatlog AI 重新初始化 → `ChatlogAI.exe --initialize`」（`ChatlogAI.iss` L98），`App.xaml.cs` 也有对应分支。

**仍未处理**

- `教程/`（`首次使用说明.txt` + 4 张 PNG）整篇是网页版 + `.bat` 口径，已失效，待决定删除或重写。
- `README.md` 已按桌面版口径重写（其中"数据与隐私"一节补上了"启用自动回复/定时发送时程序会发送消息"这一此前缺失的事实）。
- `PROJECT_SCAN.md` / `API_REFERENCE.md` 中引用 `app/static`、`launcher.py` 的位置已加退役标注，其余按历史对照保留。
- `client/**` 里约 15 处"等价 launcher.py 的 XXX"溯源注释**保留**（它们记录的是"这块从前哪来的"，仍有价值）。

**一条操作教训（写给未来的窗口）**：对**同一个文件**的多处修改**必须串行**提交，**不能在同一条消息里并行发出多个编辑**——并行编辑各自基于同一份快照写回整个文件，最后一个覆盖前面的，且**不会报任何错**。本次 `server.py` 就被改成了"半改"状态（`STATIC_DIR` 定义已删、引用它的 mount 块还在 → `NameError`，启动即崩），靠导入自检才发现。

### 7.17 T19 账号切换：冻结的实现规格

**为什么"切换"本身不难**：`wechat_account` 只被两处消费——① `WeChatAdapter(base_url, account)`，每个方法都 `_resolve_account()` 后把 `"account"` 塞进请求参数（adapters.py L214/L294/L330/L369）；② `_auto_reply_account_key()`，用来选 `config/auto-reply/{account}.json`。而 `get_wechat_adapter()` 与引擎的 `_get_adapter()` **都以 `(wechat_api, wechat_account)` 为缓存键**（server.py L398/L960），key 一变就重建 adapter。
→ **不需要重启数据源，也不需要重启本服务。** 切换的主体是：写配置 + 让几处缓存失效 + 重置引擎指纹 + 闸门。

**真正难的是安全**：程序能改"读哪个账号的数据"，但**无法改也无法确认"微信客户端现在登录的是谁"**（上游 `GET /api/current-account` 探的是企业微信登录目录，对个人微信恒返回 null，见 §14.4.1.1）。危险窗口 = 数据源已指向 B、客户端还登录着 A，此时若 A 里恰有同名会话，回复会**发错人且不可撤销**。定时发送同理（也走键盘模拟）。

**已确认的 4 条设计决策**：① 闸门 = 运行态暂停 + 落盘待确认标记；② 入口 = rail 顶部头像（身份指示器）+ 设置模块一行；③ 授权页 = 系统默认浏览器打开；④ 切换成功后界面**全部清空并重载**。

#### 后端契约（T19-A，窗口实施时逐字实现）

| 端点 | 请求 | 响应要点 |
|---|---|---|
| `GET /api/wechat/accounts` | — | `active` / `defaultAccount` / `accounts[]` / `pendingSwitch`。每项：`account` `name` `keyReady` `switchable` `hasDecryptedDbs` `isActive` |
| `POST /api/wechat/account/resolve` | `{account}` | `{authorizeUrl}`。**直接返回上游 UI 根地址 `{wechat_api}/`**（与 `launcher.py` L423 的既有行为一致），记成"未找到专用授权端点，降级为上游界面首页"。**不要用关键字去探测 openapi.json**：`/api/chat/accounts` 里就含 `account`，必然误命中，把用户送到一个返回 JSON 的页面；而且"授权页"是浏览器 UI，本来就不会出现在 openapi 里。 |
| `GET /api/wechat/account/resolve-status?account=` | — | `{keyReady, switchable, message}`（轮询用，从 accounts 里取该账号） |
| `POST /api/wechat/account/switch` | `{account}` | `{previousAccount, active, pendingConfirm: true, message}` |
| `GET /api/wechat/account/pending` | — | `{pending, account, previousAccount, requestedAt}` |
| `POST /api/wechat/account/confirm` | — | 删标记文件 + 放开闸门 |

- 账号列表的数据源是上游 `GET /api/chat/accounts` 的 **`accountInfos[]`（对象数组，含 `keyReady`/`switchable`/`hasDecryptedDbs`）** + `default_account`。**不要拿 `accounts`（字符串数组）当对象解析**——那正是 `launcher.py` 的既有缺陷。`isActive` = `account == config.wechat_account`（config 为空时 = `default_account`）。
- `POST /api/wechat/account/switch` 的**执行顺序即安全设计，不许调换**：
  1. 校验（账号存在、`keyReady==true`，否则 400 + 明确文案；已是当前账号则 400）
  2. **先置闸门**（暂停发送）——必须在第 3 步之前
  3. `with _AUTOMATION_LOCK:`（保证没有在途的键盘自动化动作；同时互斥配置写入，避免与"保存定时任务"互相覆盖）
  4. 原子写 `config/config.json` 的 `wechat_account`
  5. **清空引擎里所有"账号级 / 微信客户端状态"缓存**：`_baselined` / `_signatures` / `_fingerprints` / `_last_reply_at`（不重置会导致"刚切完就自动回复一条"），以及 **`active_chat_name`**。
     `active_chat_name` 是 T19-A 实作时发现的同类风险（§7.17 初版漏了）：`automation.send_wechat_message` 在"目标会话名 == 记录的前台会话名"时**会跳过搜索直接粘贴**；切换后这个值还是旧账号的，若新账号当前打开的会话恰好同名，消息就粘进了别的会话——与"发错人"同类。
     → 顺带**系统排查一遍**：凡是缓存了"微信客户端当前状态"或"账号级配置"的字段，列成清单逐项判断是否需要在切换/确认时重置。**"记住微信客户端状态"这类缓存，切换账号后一律可疑。**
  6. 清 adapter 缓存，保证下一轮立刻用新账号
  7. 写 `config/pending_account_switch.json`（落盘，跨重启有效）
  8. 返回响应

#### 闸门语义（T19-A）

- 状态载体：`config/pending_account_switch.json` 是否存在。存 `{account, previousAccount, requestedAt}`。**启动时读一次缓存在内存**，确认时清掉，不要每轮读盘。
- 作用点是**"发送动作之前"，不是"检测之前"**：检测照常（用户能看到"有新消息但没回"），只在真正调 `send_wechat_message` 前拦。
- **闸门覆盖所有"走键盘模拟的发送"，不只两个引擎**：待确认状态的含义就是"程序不确定微信客户端登录的是谁"，此时**任何**键盘模拟发送都可能发错人。所以 `POST /api/reply/send` 这类手动发送路径同样要拦住（返回 409 + 明确文案，告诉用户原因是"账号切换待确认"）。
  实现上做成**一个共享谓词**（如 `_ensure_send_allowed()`），在 3 个调用点使用：两个引擎（**生成前**调用，拦住则不生成、省掉模型调用）+ 手动发送端点（拦住则 409）。不要在两处各写一份判断逻辑。
- `AutoReplyEngine`：**闸门检查放在 `_reply_to` 的最开头，在 `generate_reply_text` 之前**——否则暂停期间每轮都会白烧一次真实模型调用。拦住时记日志并 return，**不记冷却时间**。
  **签名照常更新，被拦下的那条消息视为"已消费"、确认后不补发**。理由：① 与定时发送的"跳过本次不补发"语义一致；② 补发更危险——用户刚登录 B 就去补发一条十几分钟前的回复，万一登录失败或登错账号，补发正好打在最危险的时刻。
- `ScheduledSendEngine`：到点要发时拦住 → 记日志 + **跳过这一次（不补发）**，写入文档。
- 状态端点补 `sendPaused` / `sendPausedReason` 两个字段（`/api/auto-reply/status`；定时发送若有独立状态端点则同样补），否则界面会显示"运行中"却什么都不发，用户无从理解。
- **日志必须记**，否则用户以为"漏回了"。

#### 客户端（T19-B，下一个窗口）

- rail 顶部头像 = **当前账号指示器**（显示当前账号的简称/首字母；`pending` 时叠加警示标记），点击打开**模态** `AccountWindow`。
- `AccountWindow`：账号列表（`keyReady` 明示）→ 未就绪可点「打开授权页」（系统浏览器）→ 就绪可点「切换」→ **切换后窗口不关，显示待确认区**（"请在微信客户端登录 X，完成后点「我已切换」"）→ 点确认才恢复发送。
- 设置模块加**一行只读**：当前微信账号 + 「管理」按钮（打开同一个窗口）。
- **启动时若 `pending==true` → 自动打开 `AccountWindow` 并停在待确认区**（否则用户会看到"自动回复不工作"却找不到原因）。
- 切换成功后**全部清空并重载**：会话列表、当前会话、消息、AI 多轮对话、余额；然后按新账号重新拉。
- **启动时两个模态窗口必须串行**：主窗口首帧 → （免责声明，若未被"不再提示"跳过）→ **它关闭之后** → 查 pending → （账号窗口）。**不要各起一个 `Dispatcher.BeginInvoke`**：`ShowDialog` 会跑嵌套消息循环，队列里的第二个回调会在第一个模态窗还开着时就执行，两个模态窗并发、互相压住，用户看到一个说不清顺序的界面。
- **头像"简称"的取数有时序问题**：显示名来自上游 `accounts[].name`，而上游是客户端启动后才拉起的 → 启动早期必然是空。降级显示（account 尾部字符或首字母），并在上游就绪后、或打开账号窗口后刷新一次；**不要为此新增第 5 个轮询**（现有 4 个已经够多，而账号名几乎不变）。`pending` 来自本项目后端、不依赖上游，所以头像的警示标记一开始就能正确。
- **账号窗口防重入**：连续点头像不要开出两个窗口。
- `sendPaused` / `sendPausedReason` **必须有显示位置**，否则别加这两个字段：放在设置模块那一行（"自动发送：已暂停 — <原因>"）。

#### 顺手修既有缺陷（T19-A）

`launcher.py` 的 `list_wechat_accounts()` 按"对象数组"解析上游 `accounts`（实为字符串数组），导致这段"接口优先"逻辑**永远拿不到数据、一直在走扫盘兜底**（§14.4.1.1）。修成"优先用 `accountInfos[]`，为空或失败再回退到原有扫盘逻辑"，让启动期与运行期用同一套判据。

#### 真机验证记录（2026-09-11，单账号）

用户按"手工造 `config/pending_account_switch.json` → 重启客户端"的方式，验完了整条"待确认"链路，**5 项全过**：

| # | 验证点 | 结果 |
|---|---|---|
| 1 | 启动时自动弹出账号窗口，且停在待确认区（而非账号列表） | ✅ |
| 2 | 图标栏顶部头像出现警示标记 | ✅ |
| 3 | 设置模块显示「自动发送：已暂停 — <原因>」 | ✅ |
| 4 | **守护对象真发来消息时不触发回复**（闸门拦住了发送，日志有记录） | ✅ |
| 5 | 点「我已切换」后恢复发送，且标记文件被删除 | ✅ |

第 4 项是关键：**"防发错人"的那道闸门从此有实测背书**，不再是"设计如此、未验证"。

**仍未验证（客观限制）**：真的切到第二个微信账号——数据源是否真读新账号、消息是否发给新账号里的人、同名会话会不会串。这需要第二个**已授权**的账号，只能靠第三方反馈。

**复现这条验证时的两个坑**：

1. 标记文件必须是**无 BOM 的 UTF-8**。PowerShell 5.1 的 `Set-Content -Encoding utf8` 会写 BOM，Python 按 `utf-8` 读会解析失败；请用 `[System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))`。
2. 闸门是**启动时读一次并缓存在内存**的（见上面的闸门语义），所以必须在**关掉客户端之后**再改那个文件，运行中改不生效。

### 7.16 回复间隔（同一会话的最小回复间隔，T26）

**背景（实测证据）**：引擎判定"有新消息"的粒度是"最后一条**对方**消息的 id"，因此对方连发 N 条会各自触发一次生成。实测（守护对象 `djct-后端开发-叶启欣`）：

```
IN 14:16:05 "ok"    IN 14:16:10 "你是谁"
ME 14:16:11 "<回复内容>"     ← 回复 ①
ME 14:16:20 "<回复内容>"     ← 回复 ②，与 ① **一字不差**
```

两条回复文本完全相同 → 证明第二次生成的输入与第一次一致 → **回复 ② 是纯多余的刷屏**。

**冻结的契约（前后端必须逐字一致，别各写一套）**：

| 项 | 值 |
|---|---|
| 字段名 | `reply_cooldown_seconds`（int） |
| 存放位置 | 与 `context_count` / `reply_prompt` **同级**，即 `config/auto-reply/{account}.json`，由 `GET`/`POST /api/auto-reply/config` 读写 |
| 取值范围 | 服务端夹到 **[0, 600]**；默认 **10**；**0 = 不限制** |
| 语义 | 配置值是**按账号一份**（不是每个守护对象一份）；但**冷却计时按会话分别记**——给 A 回复过不会压住给 B 的回复 |
| 冷却期内再触发 | **只更新签名、不回复**，并写一条日志（如"在冷却期内，本条不单独回复"），让用户看得出是"跳过"而不是"漏回" |

**实现约定**：冷却期内**丢弃**，不做"挂起合并"（后者要把待处理状态从 `_handle_changed` 挪到 `_poll_once`，因为指纹没变时进不了 `_handle_changed`，多一个状态机；而实测已证明第二次触发的输入与第一次相同，丢弃没有任何信息损失）。

**分工**：客户端半边（`Models/AutoReplyModels.cs` / `ViewModels/AutoReplyViewModel.cs` / `Views/AutoReplyWindow.xaml`）**已完成**；后端半边（`server.py` 的默认值 + 清洗夹取 + `_handle_changed` 闸门）**并入 T25** 一起做。**后端落地前，这个设置项在界面上可填可存但不会生效**（`POST` 对未知字段是静默忽略）。

### 7.1 Bootstrap 层文件清单（T4，已完成）

目录：`client\ChatlogAI.Client\Bootstrap\`

| 文件 | 职责 | 对应的 `launcher.py` |
|---|---|---|
| `AppPaths.cs` | 路径单一事实源；只读 RootDir 与可写 DataDir 分离 | L24-L47 常量区 |
| `ConfigBootstrapper.cs` | 建目录、补齐 config/prompts、必需文件校验、原子写 JSON | `ensure_directories`(L169) / `ensure_config_files`(L148) / `require_paths`(L178) / `load_json`(L126) / `save_json`(L139) |
| `PortGuard.cs` | connect 探测端口占用 | `ensure_port_free`(L185) |
| `JobObjectHost.cs` | P/Invoke Job Object，`KILL_ON_JOB_CLOSE` | `ProcessJob`(L86-L123) |
| `ChildProcess.cs` | 子进程启动 + 日志重定向 + taskkill 收尾 | `start_process`(L206) / `stop_process_tree`(L223) |
| `WechatAuthWatcher.cs` | 密钥文件轮询判定授权 | `has_wechat_key`(L313) / `wait_wechat_authorization`(L427) |

**两处刻意偏离 Python 版**（需知悉）：

1. `RequirePaths()` 额外校验 `runtime\python\python.exe` 与 `app\server.py`。原 Python 版只校验微信后端三件套，因为它的解释器就是自己；.NET 客户端是另一个进程，缺 Python 会以更晚、更难懂的方式失败。
2. `AppPaths` 支持 `CHATLOG_ROOT_DIR` / `CHATLOG_DATA_DIR` 两个环境变量覆盖根目录，用于开发期把 exe 直接指向仓库，**正式安装运行时两者都不设置**。

### 7.2 T5 文件清单（启动编排 + 启动窗口，已完成）

| 文件 | 职责 | 对应的 `launcher.py` |
|---|---|---|
| `Bootstrap/StartupOrchestrator.cs` | 阶段 0-5 状态机 + `ShutdownAsync` + 看门狗 | `run()`(L442-L546) / `start_wechat_service`(L286) / `server_command`(L436) |
| `Bootstrap/StartupResult.cs` | 终态三态：成功 / 失败 / **用户取消**（区分后两者，避免取消也弹错误框） | — |
| `Bootstrap/WechatAccount.cs` | 账号 record，`Display` 逐字对齐 Python 的 `"%s（%s）"` | — |
| `Bootstrap/WechatAccountEnumerator.cs` | 账号枚举：接口优先 → 扫盘兜底 → 去重 | `list_wechat_accounts`(L327-L370) |
| `Bootstrap/IStartupInteraction.cs` | 编排层与 UI 的**唯一**接口（编排层不依赖任何 View 类型） | 原 `print` / `input` 交互点 |
| `Views/StartupWindow.xaml(.cs)` | `IStartupInteraction` 的 WPF 实现；浅色主题、日志区、授权遮罩、账号选择遮罩 | `prompt_account_selection`(L397) |
| `Views/MainWindow.xaml(.cs)` | T5 时是**临时占位**，仅用于验证启动/生命周期闭环；**已由 T8 替换为正式三栏主界面**，见 §7.5 | — |
| `App.xaml.cs` | 总入口：解析 `--initialize`、挂接编排、退出清理兜底 | `run()` 的入口部分 |

**T5 相对 Python 版的三处刻意偏离**（需知悉）：

1. 提权判定用 .NET 8 内置的 `Environment.IsPrivilegedProcess`，不再 P/Invoke `shell32.IsUserAnAdmin`（R11）。
2. **`wechatdb_broker.exe` 也纳入 Job Object**，补齐 R2（原 Python 版漏了）。
3. broker 的 stdout 落盘 `logs\wechat-broker.log`，不再是 `DEVNULL`——便于事后排查微信源启动失败。

**T5 遗留**：
- ~~`Views/MainWindow` 是占位窗口，真正主界面属 T8~~ → **已销账**：T8 已替换为正式三栏主界面，见 §7.5。
- ~~尚未做真机运行验证~~ → **已通过，见 §7.4**。

### 7.3 T7 文件清单（REST 客户端 + 数据模型，已完成）

| 文件 | 职责 |
|---|---|
| `Services/ChatlogApiClient.cs` | 22 个接口中界面会用到的全部（除 `/` 与 `/static/*`）；统一传输层 + 错误解析 |
| `Services/ChatlogApiException.cs` | 携带 HTTP 状态码的异常；`IsUnreachable` 用于区分「业务错误」与「后端已死」 |
| `Models/ChatModels.cs` | `ChatSummary` / `ChatMessage` / 列表响应 + `timestamp` 秒毫秒归一 |
| `Models/ProviderModels.cs` | 供应商 / 预设 / 余额 / `SaveProviderRequest`（含「api_key 留空 = 沿用旧值」） |
| `Models/AiModels.cs` | `SummarizeRequest/Response` / `HistoryItem` / 历史读写 |
| `Models/AutoReplyModels.cs` | 配置 / 守护对象 / 快捷键 / 状态日志 / `AutoReplyConfigPatch`（部分更新） |

**T7 的四个关键设计决定**（改动前先读这里）：

1. **成功判定不能写死 `status == "success"`**：`/api/config` 根本没有 `status` 字段，`/api/health` 是 `"ok"`。因此判据是 `status ∈ {success, ok, 缺失}`，收敛在 `EnsureStatus()` 一处。
2. **错误文案解析兼容 `detail` 是数组**：FastAPI 的 422 返回 `{"detail":[{"msg":...}]}`，而业务错误返回 `{"detail":"..."}`。`TryReadText()` 两种都处理。
3. **部分更新用「可空 + 序列化忽略 null」表达"该字段不变"**：这是 `AutoReplyConfigPatch` 与 `SaveProviderRequest.ApiKey` 的实现方式。特别注意 `Enabled` 必须是 `bool?`——若是 `bool`，只想改提示词时会把 `enabled` 一起写成 `false`。
4. **两种超时**：普通请求 30s；会真实调用模型的接口（validate / summarize / balance）180s。客户端超时必须**大于**服务端给模型的 60s，否则会先于服务端误报超时。

**T7 遗留**：~~`ChatlogApiClient` 尚未接入 `App` 生命周期（等 T8 主界面一起挂），当前 `Dispose()` 无人调用。~~ → **已销账**：`App.OnStartup` 阶段 5 构造 `ChatlogApiClient` 并交给 `MainViewModel`；正常退出（`RequestExitAsync`）与 `OnExit` 兜底两条路径都会 `Dispose()`。退出顺序见 §7.5 第 3 条。

### 7.4 T5 真机验证记录（已通过）

**环境与方式**（开发期，依 §13.4）：管理员 PowerShell；先设
`CHATLOG_ROOT_DIR=d:\code\chatlog-ai-portable`、`CHATLOG_DATA_DIR=d:\code\chatlog-ai-portable\client\out-data`，
再 `& "...\bin\Debug\net8.0-windows\ChatlogAI.exe"` **前台运行**（必须 `&`，用 `Start-Process` 看不到输出）。

| # | 验证项 | 结论 | 证据 |
|---|---|---|---|
| 1 | 阶段 0–5 编排全程贯通 | ✓ | 启动窗口 → 微信源 → 授权 → 后端 → 主窗口，无「缺少运行文件」 |
| 2 | `requireAdministrator` 提权（R11） | ✓ | 管理员控制台下启动成功 |
| 3 | `PYTHONPATH` 等环境注入生效 | ✓ | `import fastapi` 未失败，`python.exe` 正常监听 18765 |
| 4 | 端口归属正确 | ✓ | 18765 → `python.exe`；10392 → `wechat-backend.exe` |
| 5 | 授权链路（含真实微信数据源） | ✓ | `wechat.log` 出现 `Found valid key!`、`GET /api/keys` 200、`GET /api/decrypt_stream` 200 |
| 6 | 进程全部归属 Job Object | ✓ | 所有后代进程均在 `ChatlogAI.exe` 的进程树内 |
| 7 | **优雅退出（点窗口 X）** | ✓ | `& exe` 后提示符数秒内返回 |
| 8 | 无残留进程 / 端口 / 关闭标记 | ✓ | 验证命令：端口无 `Listen`、进程列表空、`.shutdown-requested` 不存在 |

**进程树实测**（一次正常启动）：

```
ChatlogAI.exe                       ← 本客户端
├─ wechatdb_broker.exe              ← 本客户端预起（--database-root 指向 DataDir）
├─ wechat-backend.exe               ← 本客户端起
│   └─ wechat-backend.exe           ← 上游自派生，占 10392
│       └─ wechatdb_broker.exe      ← 上游自派生，指向真实微信库
└─ python.exe                       ← 本客户端起，占 18765
```

即：本项目只显式启动 **2 个**子进程，与 [launcher.py L286-L310](file:///d:/code/chatlog-ai-portable/app/launcher.py#L286-L310) 一致；多出来的 3 个是**上游 `wechat-backend.exe` 自身的设计**（自派生后端 + broker），**不是本项目的 bug**，无需"修"。

**两条必须记住的认知点**：

1. **Windows Job 的成员身份会被后代自动继承**——本客户端只 `AssignProcessToJobObject` 了直接子进程，但上游派生的孙进程也自动成为 Job 成员。这是 R1/R2「关窗口即全死」一次到位的机制保证，不需要逐个 `Assign`。
2. **GUI 子系统进程不附着控制台**——关闭控制台窗口不会杀掉 `WinExe`。因此"进程消失"只可能是"被单独结束"，据此可区分「点 X 优雅退出」与「任务管理器强杀（Job 兜底）」两种路径；本次验证的是**前者**（下次真实操作路径）。

**顺带确证的环境事实**（此前探测失败的原因）：本机微信**不在 C 盘**，装在 `D:\` 下（`Weixin.exe` 与 `xwechat_files` 数据根均在 D 盘）。这正是之前按 `C:\Program Files\Tencent\Weixin`、`Documents\xwechat_files` 探测均落空的原因。客户端**不做**微信路径硬编码，一律由上游 `wechat-backend.exe` 自行探测，故与本事实无关，仅作排查时的背景知识。

**一处已知观察（暂不处理）**：我们预起的 broker 使用 `DataDir\data\wechat\data`，而上游真正使用的 broker 指向真实微信库目录，说明 `WECHAT_TOOL_DATA_DIR` 更像"提示"，会被上游自行覆盖。Python 版行为完全相同，保持等价即可。

### 7.5 T8 文件清单与设计要点（主界面，已完成）

| 文件 | 职责 |
|---|---|
| `ViewModels/ObservableObject.cs` | `INotifyPropertyChanged` 基类。**手写，不引第三方 MVVM 库**——Trae 沙箱会拦 NuGet，多一个包就多一处构建风险 |
| `ViewModels/RelayCommand.cs` | `RelayCommand` / `AsyncRelayCommand`；后者执行期间 `CanExecute=false`，用于防重入（如「生成分析」连点） |
| `ViewModels/ChatItemViewModel.cs` | 会话行。**可变**，理由见下方第 1 条 |
| `ViewModels/MessageItemViewModel.cs` | 消息行。**不可变**（消息一旦落库不会再变） |
| `ViewModels/AiThreadItemViewModel.cs` | AI 对话行。`Content` 是 `FlowDocument?`，**用户消息为 `null`**（用户输入不渲染 Markdown） |
| `ViewModels/MainViewModel.cs` | 主 VM：会话 / 消息 / AI 分析 / 四组轮询 / 状态栏 / 筛选条件 |
| `Services/MarkdownRenderer.cs` | Markdig → `FlowDocument` |
| `Views/MainWindow.xaml(.cs)` | ~~三栏深色界面~~ → **T16 后为「56px 图标栏 + 内容宿主」**，内容由 `Views/Modules/*` 承载（见 §7.10）。只负责「看」；VM 的构造与销毁都归 `App` |
| `App.xaml.cs` | 阶段 5 构造 `ChatlogApiClient` + `MainViewModel`；退出统一收尾 |

**已接通的主链路**：会话轮询(10s) → 选中会话 → 消息轮询(5s) → 时间/条数双重筛选 → 四个预设 / 自定义提问 → `/api/summarize` + `/api/history` 多轮上下文 → `FlowDocument` 渲染。另两组轮询：自动回复状态(3s)、余额(60s)。

**T8 的六条关键设计决定**（改这块代码前先读这里）：

1. **`ListBox.SelectedItem` 按对象引用比对** → 会话行 VM 必须「实例复用 + 原地更新」：轮询刷新时按 `id` 复用旧实例只改字段，集合重建用 `Chats.Move` 而非 `Clear`（见 `MainViewModel` L540）。否则每次轮询都会丢掉用户的选中项，进而触发「取消选择」把消息区清空。
2. **`FlowDocument` 只能有一个父级** → AI 对话区用 `ScrollViewer + ItemsControl`，**不能**用 `ListBox`：后者的容器回收会让同一个 `FlowDocument` 先后挂到两个 `FlowDocumentScrollViewer`，抛「文档已有父级」。AI 轮数少不需要虚拟化；中栏消息区 2000 条仍必须是 `ListBox`（虚拟化）。
3. **退出顺序是刻意排的**：`_mainLifetime.Cancel()` + `MainViewModel.Dispose()`（停定时器与在途请求）→ `orchestrator.ShutdownAsync()`（停子服务）→ `_apiClient.Dispose()`。反过来的话，服务已经关掉、界面每 5 秒仍会打一次注定失败的请求。
4. **VM 内所有 `await` 一律不加 `ConfigureAwait(false)`**：四组 `DispatcherTimer` 的 Tick 本身就在 UI 线程，加 `false` 反而把续体甩到线程池。唯一需要 `false` 的是 `StartupOrchestrator.ShutdownAsync` 内部——`App.OnExit` 要在 UI 线程上同步 `.GetAwaiter().GetResult()` 等它，那条链必须全程 `ConfigureAwait(false)` 才不死锁（`App.xaml.cs` L130 有注释）。
5. **筛选条件的 setter 不触发重新拉取**：只做取值域夹紧 + `OnPropertyChanged`。真正重取由「应用」按钮的 `ApplyFilterCommand` 触发，否则用户每敲一个字符就打一次后端。
6. **`Math.Round(double)` 默认「四舍六入五成双」** → 要对齐原前端 JS 的 `Math.round`，必须显式传 `MidpointRounding.AwayFromZero`（`MainViewModel` L689）。

**Markdown 表格：本阶段不解析（取舍记录）**

`MarkdownPipeline` 用默认配置，`MarkdownRenderer.AppendBlock` 没有 `TableBlock` 分支，因此表格会退化成一整段带竖线的文本。

- **为什么这轮不做**：T8 的交付目标是「骨架先跑通」；四个预设提示词（综合总结 / 会议纪要 / 待办事项 / 话题热点）要的是**标题与列表**，表格属边缘场景。
- **代价**：若提示词或模型自行输出表格，用户会看到一坨竖线文本，观感差。
- **将来要补的做法**：管道改 `new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()` 打开表格扩展，`AppendBlock` 加 `case TableBlock table:` 分支，用 `Table` + `TableRowGroup` + `TableRow`/`TableCell` 构造；**浅色主题下必须显式设 `BorderBrush`**，否则默认无框线看不出是表格。
- **同类的另两处有意退化**：嵌套列表被**展平到同一层**（WPF `ListItem` 只有接受 `Paragraph` 的构造函数，子列表塞不进父项做缩进）；图片**不下载**，退化成链接文本。

**T8 遗留**：
- **退出二次确认尚未实现**。当前退出路径是「点退出 / 关窗 → 拦下 → 停服务 → 退出」，没有确认弹框；而下文「与原前端的对应关系」明确要求二次确认（`/api/shutdown` 会整体退出）。计划随 T11 之后一并补。
- T9 / T10 的入口按钮已留在右栏，点击只弹「正在开发中」；T11（导出）已接通，见 §7.7。**（已过时：T9 / T10 / T11 均已接通，见 §7.9 / §7.8 / §7.7）**

### 7.6 T15 UI 主题改造（浅色 + 微信绿，已完成）

**动机**：T8 交付的深色界面被用户明确否定，三条原话：「时间范围那里根本看不清」「UI 可以亮一点」「这个蓝色都什么鬼东西」。

**改动清单**：

| 文件 | 改动 |
|---|---|
| `Themes/Theme.xaml` | **新建**。全应用唯一的配色与控件样式来源，共 12 节：颜色 Brush → 转换器 → Button / TextBox / ComboBox / ComboBoxItem / ListBoxItem |
| `App.xaml` | `Application.Resources` 挂载 `Themes/Theme.xaml`（`MergedDictionaries`），全局生效 |
| `Views/MainWindow.xaml` | 删掉整个 `<Window.Resources>`（66 行硬编码样式，含 `DarkListBoxItem`），所有色值改 `DynamicResource` |
| `Views/StartupWindow.xaml` | 删掉自带的两份 Button 样式，改用 App 级样式；硬编码色值全改 `DynamicResource` |
| `Services/MarkdownRenderer.cs` | 7 个写死的 `static readonly` Brush 改为**惰性求值属性**，按 key 从主题取色 + 深色兜底 |

**三条吐槽的根因（不是"审美问题"，是三个具体缺陷）**：

1. **「看不清」= 对比度不达标 + 禁用态糊掉**。标签色 `#8A8F9C` 压在 `#1E1E24` 上只有约 2.6:1（WCAG AA 要求小字号 ≥ 4.5:1）；控件与背景只差一两档灰，边界消失。**真正元凶是禁用态**：选「不限」时右侧两个控件 `IsEnabled=false`，WPF 默认模板整体乘一个透明度系数，深色底上再乘一次几乎归零。
2. **「亮一点」** → 整体改浅色。此时必须重建整套对比度体系（见 `Theme.xaml` 第 1、2、5 节注释给出的实测比值）。
3. **「蓝色」= `#FF2F6FEB`**。饱和度过高、大面积铺满、且与产品语义毫无关联。新方案拆成两组：`AccentBrush #07C160` 只做点缀（状态点/聚焦边框），`AccentFillBrush #0A7F45` 才做填充配白字——因为 `#07C160` 配白字只有约 2.4:1，连非文本 3:1 都够不上。自己发的消息改用 `AccentSoftBrush` 浅绿底，不再整块铺高饱和色。

**四个 WPF 坑（改这套样式前必读，全部实测踩过）**：

1. **`ControlTemplate.Triggers` 优先级 > `Style.Triggers`**。Aero2 默认模板内部已写 `IsEnabled=False` 的 Foreground/透明度 Trigger，**只改 Style 改不动禁用态** → TextBox / ComboBox 必须自带完整 `ControlTemplate` 才能接管。这就是坑 1 的直接原因。
2. **`Style.Triggers` 优先级 > 派生样式的 Setter**。基样式写了 `IsMouseOver→灰底`，派生样式（`PrimaryButton`）设 `Background=绿`，一悬停照样被打回灰底 → **两组状态色必须各自完整写一遍**（见 `Theme.xaml` §9 注释）。
3. **ComboBox 弹层继承陷阱**：`ComboBox.Foreground` 沿 `TextElement.Foreground` 继承到 `ComboBoxItem`，弹层默认浅色 → 会出现「白字白底」。弹层底色必须显式指定。另外展开是靠模板里的 `ToggleButton`（`IsChecked` ↔ `IsDropDownOpen` 双向绑定）驱动，**不是**靠代码。
4. **XML 注释里不能出现 `--`**（`MC3000`）。注释分隔线用 `=====`，不能用 `-----`——`XamlCompiler` 会把 `--` 当注释结束符。这条是本次唯一一次编译失败的原因。

**顺带修掉的一个遗留缺陷**：`Theme.xaml` 的 TextBox 模板原先把 `PART_ContentHost` 的滚动条可见性**硬编码为 Hidden**，多行框（启动页 `LogBox`）会失去滚动条。现改为 `{TemplateBinding VerticalScrollBarVisibility}` 透传，基样式默认 `Hidden`，多行框自行设 `Auto`；`VerticalAlignment` 同样改为透传 `VerticalContentAlignment`，多行框设 `Top` 即可从顶部排版。

**新增的配色约束（后续加界面必须遵守）**：新颜色一律先加进 `Theme.xaml` 再引用，视图里不许再出现字面量色值；文字色只用 `TextPrimaryBrush` / `TextSecondaryBrush` / `TextMutedBrush` 三档（已按 AA 标准选好）；品牌绿只做点缀，填充一律用 `AccentFill*`。

### 7.7 T11 文件清单与设计要点（导出聊天记录，已完成）

实现规格见 §8。四个新增文件各管一件事：**取数（VM）→ 排版（ChatExporter）→ 落盘（VM 调 View 弹框）→ 显示（ExportWindow）**。

| 文件 | 职责 |
|---|---|
| `Models/ExportModels.cs` | **新建**。`ExportFormat` 枚举 + 下拉选项类型（`ExportFormatOption` / `ExportTimeUnitOption`）+ `ExportContext`（主界面状态快照）+ `ExportRange`（生效范围，兼作 JSON 字段）+ JSON 投影（`ExportChatInfo` / `ExportMessage` / `ExportPayload`）+ 格式化产物 `ExportDocument(Text, WriteBom)` + 落盘请求 `ExportSaveRequest` |
| `Services/ChatExporter.cs` | **新建**。无状态静态格式化器，**完全不碰 UI / 网络**：`MaxMessages=2000`、时间范围构造与折算、`FilterByRange`、四种格式的 `Format` 分支、默认文件名与 `SaveFileDialog.Filter` 串、HTML 内联样式常量 |
| `ViewModels/ExportViewModel.cs` | **新建**。收集选项（格式 / 独立时间范围 / 含 AI 历史）→ 取数 → 排版 → 落盘；把"要选路径"和"该关窗了"两件事抛给 View |
| `Views/ExportWindow.xaml(.cs)` | **新建**。对话框：格式 / 时间范围 / 含 AI 历史 / 状态 / 按钮 五区；View 侧挂 `SaveFileDialog` 与 `Close()` |
| `Views/MainWindow.xaml.cs` | **改（唯一一处共享文件改动）**：`OnExportClick` 由占位改为真实实现，外加 1 个 `using ChatlogAI.Client.Models;`。**未触碰** `OnModelSettingsClick` / `OnAutoReplyClick`（T9/T10） |

**七条关键设计决定**（改这块代码前先读这里）：

1. **导出独立取数，不复用主界面消息列表**。对话框里的时间范围可以与界面筛选**不同**（这正是本次确认的设计），且导出上限 2000 条可能多于界面"最近 500 条"的显示窗口。复用界面数据只会导出"看得见的那一部分"，与用户选的范围对不上。
2. **对话框携带独立时间范围，且刻意不传「条数」**。`ExportContext` 里只快照 `HasTimeLimit / TimeUnit / TimeValue`，**没有**"条数"字段——导出语义是"该时间范围内的全部消息（上限 2000 条）"，界面的"最近 N 条"只是显示窗口，不该变成导出的隐含限制。
3. **`ExportContext` 是"点下按钮那一刻"的一次性快照**。导出期间主界面仍在 5 秒轮询，会话与筛选随时可变；直接传 `MainViewModel` 会出现"对话框里写着 A 会话、导出的却是 B"。
4. **`ChatExporter` 是纯函数，且刻意重复了主 VM 的两小段逻辑**（时间戳归一化、时间范围裁剪）。主 VM 是 T9/T10 两个并行改造窗口的改动热点，导出侧要复用它就得把它改成 `public`，等于把两件互不相干的功能焊在一起。重复 5 行换"改动面完全不相交"，划算。
5. **客户端必须二次过滤（`FilterByRange`）**。服务端 [server.py](file:///d:/code/chatlog-ai-portable/app/server.py) 的 `filter_messages` 对**解析不出时间戳**的消息是**保留**的，而主界面 `ApplyFilters` 是**丢弃**的——两边判据不同。只靠 `recent_hours` 参数会出现"导出条数比界面多"。导出侧在客户端对齐到**界面口径**。
6. **MVVM 边界**：VM 不引用 `Microsoft.Win32`。选路径用**委托属性** `SaveFilePicker`（需要"调用并拿到返回值"，用事件还得再配回调字段，反而更绕）；关窗用事件 `CloseRequested`；`ExportWindow.ResultMessage` 作为"导出是否成功"的**唯一出口**——比 `DialogResult` 的 true/false 两态语义更明确（它是"成功文案 or null"）。与 `MainViewModel.AlertRequested` 是同一条边界。
7. **CSV 与 AI 历史互斥（本次确认的设计）**。CSV 是固定四列的表格，塞不下 AI 对话。选中 CSV 时**禁用并清空**该勾选项、下方提示改为"CSV 为表格格式（时间/发送者/类型/内容），不含 AI 对话历史"。**不做**"留着勾、导出时忽略"——那会让成功提示写着"已导出 M 条 AI 对话"而文件里一条都没有。代价：切回其它格式后需用户重新勾选（宁可让他重勾，也不能给假结果）。判据由 `IsAiHistoryEnabled` 单点给出，XAML 只做绑定。

**几个实现细节（同样有意为之）**：

- **先弹"另存为"再取数**：用户取消时不该白跑一次网络请求，也不该出现"取完数据才发现没地方放"。
- **BOM 只由 `ChatExporter` 给判据**（`ExportDocument.WriteBom`，仅 CSV 为 `true`），VM **不再自己判断一次格式**，否则两处判断迟早漂移。CSV 带 BOM 是 Excel 中文乱码的解药；JSON / Markdown / HTML 带 BOM 反而会让严格解析器报错。
- **异常只兜三类**（`ChatlogApiException` / `IOException` / `UnauthorizedAccessException`），其余继续外抛给 `App` 全局兜底——把编码错误静默成一句提示，会让真正的问题永远查不出来。
- **`source_valid` 才算数**：状态码已由 `ChatlogApiClient` 兜住，但 `SourceValid` 才是"这条数据真来自微信"的证据；只看 HTTP 200 会把空列表写进文件（与 `MainViewModel.FetchMessagesAsync` 同一判据）。
- **CSV 按 RFC 4180 转义**：含 `, " \r \n` 的字段整体加引号、内部引号翻倍；换行写死 `\r\n`（不能跟 `Environment.NewLine`），否则有一条消息带逗号，整张表就错位。
- **文件名 `<会话名>_<时间戳>.<ext>`**：会话名先剔除 Windows 非法字符并限长 80，避免顶破 260 字符路径上限。
- **HTML 是单文件内联样式**，色值刻意与 `Theme.xaml` 一一对应（浅灰底 / 白卡片 / 深字 / 微信绿）+ `@media print` 规则，双击能看、Ctrl+P 能存 PDF；它是独立文件，引不到 `DynamicResource`，**改主题时这两处要一起改**。

### 7.8 T10 文件清单与设计要点（自动回复弹窗，已完成）

对照原前端 [app.js](file:///d:/code/chatlog-ai-portable/app/static/app.js) L678-L891 的 12 个函数与 [index.html](file:///d:/code/chatlog-ai-portable/app/static/index.html) 的 `#autoReplyModal`。接口契约见 §6，数据模型与 REST 封装（`AutoReplyModels.cs` / `ChatlogApiClient` 的四个方法）在 T7 已就绪，本次只补 UI 层。

| 文件 | 职责 |
|---|---|
| `ViewModels/GuardTargetItemViewModel.cs` | **新建**。守护候选的一行：勾选态 / 超限禁用态 / 单独提示词框显隐 / `ToModel()` 提交投影 |
| `ViewModels/AutoReplyViewModel.cs` | **新建**。弹窗的表单状态与三个命令（保存 / 测单个快捷键 / 测完整流程）；只读配置与候选会话 |
| `Views/AutoReplyWindow.xaml(.cs)` | **新建**。模态对话框（对照 `#autoReplyModal`）；View 侧只挂 `MessageBox` / `Close()` / `CancelPending()` |
| `ViewModels/MainViewModel.cs` | **改**：新增 `IsAutoReplyAvailable`、`CreateAutoReplyViewModel()`、`ReloadAutoReplyConfigAsync()`，并在 `LoadAutoReplyConfigAsync` 里读 `available` |
| `Views/MainWindow.xaml(.cs)` | **改（各 1 处）**：「自动回复」按钮加一行 `IsEnabled="{Binding IsAutoReplyAvailable}"`；`OnAutoReplyClick` 由占位改为打开弹窗。**未触碰** `OnModelSettingsClick`（T9）与 `OnExportClick`（T11） |

**六条关键设计决定**（改这块代码前先读这里）：

1. **弹窗形态 = 独立 `Window` + `ShowDialog()` 模态、`Owner` 为主窗口**。这与原前端的模态遮罩同语义，且直接复用 WPF 的键盘/焦点/居中能力；不用"主界面内嵌 `UserControl` + 自绘遮罩"，那要自己重做焦点陷阱与 Esc 行为。
2. **配置读写逻辑放独立的 `AutoReplyViewModel`，不复用 `MainViewModel`**。弹窗里的一切（勾选态、提示词草稿）都是**未提交的编辑中状态**，而主 VM 持有的是**服务端真值**；混在一个对象里就说不清"哪份才算数"，取消时也没法干净回滚。
3. **弹窗 VM 每次打开都新建**，不复用实例——否则会出现"上次取消前改了一半的内容又冒出来"。
4. **候选列表用 `ScrollViewer + ItemsControl`，不用 `ListBox`**。每行内嵌一个"单独提示词"`TextBox`，而 `ListBox` 默认虚拟化会回收容器，滚动时输入框会失焦、内容错位。
5. **上限 8 好友 + 3 群聊是"提前禁用未选中项"**（`checkbox.disabled = !isChecked && count >= max`），而不是让用户在保存时吃一个 400；**已选中项恒为可点**，否则用户没法取消勾选来腾出名额。两组各自独立计数。
6. **`CheckBox` 的主题缺口就地解决**：`Theme.xaml` 里没有 `CheckBox` 样式，Aero2 默认模板选中色是系统蓝，与微信绿主题冲突，因此在本窗口资源里私有实现 `ThemedCheckBox`（**自带完整 `ControlTemplate`**——理由见 §7.6 坑 1）。**刻意不改 `Theme.xaml`**：那是 T9/T11 的改动热点，为两个控件去动公共文件不划算。同理"危险按钮"`DangerButton` 也留在本窗口。徽标配色沿用现有两档语义色（好友=中性灰 `SubtleBrush`、群聊=浅绿 `AccentSoftBrush`），**未新增任何色值**，符合 §7.6 的配色约束。

**三处刻意偏离原前端**（都是为了堵住"静默数据丢失"，原实现是会真出事的）：

| # | 原前端行为 | 现在的行为 |
|---|---|---|
| 1 | 读配置失败时直接用内置默认值填表，用户不编辑就点保存 → **把服务端配置整份覆盖成默认值** | 读失败即禁用保存并弹一次错误。宁可这次改不了，也不覆盖 |
| 2 | `/api/chats` 失败时列表为空、文案却停在"正在加载微信会话……" → 保存会提交**空的守护列表**（等于清空） | 同样禁用保存，并给出明确的失败文案 |
| 3 | `context_count` 走 HTML `type=number`，非法输入 `Number()` 得 `NaN` → 序列化成 `null` 被后端**静默跳过** | 保存前显式校验 1–2000 并明确报错，不做静默兜底 |

**几个实现细节（同样有意为之）**：

- **`InitializeAsync` 必须串行**：候选列表要用已保存的守护对象回填勾选态，配置没回来就渲染会出现"列表渲染完了但一个都没勾上"。原前端同样是先 `applyAutoReplyConfigToForm()` 再 `loadGuardChatOptions()`。
- **`IsAutoReplyAvailable` 默认 `true`，只有服务端明确回报 `available: false` 才禁用入口**（对应 §11 检查清单"非 Windows 隐藏/禁用入口"）。反过来默认 `false` 的话，一次配置读取失败就会让按钮永久点不动，用户没有自救手段。
- **保存成功才回读配置**（`AutoReplyWindow.Saved` 标志位）。3 秒一次的 `RefreshAutoReplyStatusAsync` 打的是 `/api/auto-reply/status`，那里只有 `enabled` / `running` / `log`，**拿不到** `guard_targets` 与 `context_count`，所以不主动回读的话状态栏会先显示一段陈旧文案。
- **关窗时序**：窗口 `Closed` → `CancelPending()`（先 Cancel 再 Dispose）。因此 `InitializeAsync` 在**取 `_lifetime.Token` 之前**先检查 `IsCancellationRequested`——否则"用户在配置还没回来时就关窗"会走到下一步去读已释放的 Token，抛 `ObjectDisposedException`。
- **`InitializeAsync` 的契约是不抛异常**（失败转 `AlertRequested`，取消转提前返回），所以 `AutoReplyWindow.OnLoaded` 用 `_ = ...` 丢弃 Task，而不写成 `async void`（后者一旦有异常逃逸会直接崩掉进程）。
- **`context_count` 用 `string` 承载而不是 `int`**：WPF `TextBox` 绑 `int` 时，输入非法字符会让绑定更新**静默失败**、源值保持旧值，用户看不出自己的输入被忽略了。
- **保存是全量提交五个字段**（与 `saveAutoReplyConfig()` 一致）；`AutoReplyConfigPatch` 的可空能力保留给将来"只改单个字段"的场景。
- **↳ 遗留项（已关闭）**：`MainWindow.xaml` 中「模型设置 / 自动回复 / 导出 本轮都只留入口」那条注释（L367-L368）在 T10 落地时已过时，T10 刻意没碰。**T9 完成时已一并勘误**（改成"三个入口各自打开一个模态窗口"），见 §7.9。

### 7.9 T9 文件清单与设计要点（模型设置弹窗，已完成）

对照原前端 [app.js](file:///d:/code/chatlog-ai-portable/app/static/app.js) L462-L586 的 8 个函数与 [index.html](file:///d:/code/chatlog-ai-portable/app/static/index.html) 的 `#modelModal`。接口契约见 [API_REFERENCE.md](file:///d:/code/chatlog-ai-portable/docs/API_REFERENCE.md) §3；数据模型与 REST 封装（`ProviderModels.cs` / `ChatlogApiClient` 的 6 个供应商方法）在 T7 已就绪，本次只补 UI 层。

| 文件 | 职责 |
|---|---|
| `ViewModels/ProviderItemViewModel.cs` | **新建**。供应商列表的一行：`Title` / `Subtitle` / 使用按钮文案与可用性；另**额外暴露** `Id` / `ProviderType` / `RawName` / `BaseUrl` / `ModelName` 供"编辑"回填原始值 |
| `ViewModels/ModelSettingsViewModel.cs` | **新建**。弹窗的全部状态与命令：列表 / 预设下拉 / 表单 / 编辑态 / 验证门禁 / 保存 / 切换 / 删除；回刷主界面走构造注入的 `Func<Task>` 回调 |
| `Views/ModelSettingsWindow.xaml(.cs)` | **新建**。模态对话框（对照 `#modelModal`）；View 侧只挂 `PasswordBox` 同步、`MessageBox`、`Close()`、`LoadAsync()` / `Dispose()` 的时机 |
| `Themes/Theme.xaml` | **改**：新增第 13 节 `PasswordBox` 隐式样式（原 `app/static/` 用的是 `<input type="password">`，主题里此前只有 `TextBox`）、第 14 节 `DangerButton` |
| `ViewModels/MainViewModel.cs` | **改**：新增 `ModelSettingsRequested` 事件、`CreateModelSettingsViewModel()`、`RefreshModelStateAsync()`；`InitializeAsync` 末尾在无生效供应商时抛事件 |
| `Views/MainWindow.xaml(.cs)` | **改**：入口按钮那条注释的勘误（见 §7.8 遗留项）；`OnModelSettingsClick` 由占位改为打开弹窗、新增 `OnModelSettingsRequested`、删除已无调用点的 `ShowNotImplemented` |

**三条相对原前端的"补漏"**（原实现是会真出事的，逐条对照后补上）：

1. **补上了「编辑」**。原 `collectProviderForm()`（L475-483）从不返回 id、`saveProvider()`（L511-527）也从不传 id，所以网页版界面上写着"添加 / 保存"，实际**只会新增、不会更新**——想改一个写错的 Base URL，只能删了重加。现在引入 `EditingProviderId`：`null` 走新增、非 null 走更新（`SaveProviderRequest.Id` 传与不传即服务端的两种语义，见 API_REFERENCE §3.4）。
2. **「验证通过才能保存」是一道真门**。原前端在验证通过后把 `mpSaveBtn.disabled` 置 false，但**之后用户再改 base_url / model / api_key，按钮不会重新变灰**——等于拿着旧结论去提交新参数。现在 `SetFormField()` 里任何一项改动都作废 `IsKeyVerified`（并清掉上一轮的验证文案）。
3. **新增严、编辑宽**。新增必须验证通过（服务端对"新增且 api_key 为空"直接 400）；编辑时 Key 留空表示"沿用旧 Key"，此时**不可能**去调验证接口（`/api/providers/validate` 的 api_key 是必填），所以放开这道门禁，只校验 base_url / model 非空。**已知取舍**：若用户先填新 Key 验证通过、再手动清空 Key，会因"值变了"而重新要求验证——宁可多验一次，也不放行一个拿着旧结论的提交。

**四处分歧决定**（改这块代码前先读这里）：

1. **弹窗形态 = 独立 `Window` + `ShowDialog()` 模态、`Owner` 为主窗口**。与 T10 同一理由（见 §7.8 决定 1）。本次额外的收益是：T9 与并行开发的 T10 都改 `MainViewModel.cs` / `MainWindow.xaml.cs`，独立窗口让 T9 对 `MainWindow.xaml` 的改动为**零**。
2. **列表用 `ItemsControl` 而不是 `ListBox`**。这里没有"选中某一行"的语义——整行的三个动作按钮各自带 `CommandParameter`，用 `ListBox` 反而要额外处理选中高亮与键盘导航。
3. **`ProviderItemViewModel` 只暴露只读投影、不提供 `Update()`**。与 `ChatItemViewModel`（10 秒轮询、必须保住 ListBox 选中项身份）不同，供应商列表只在"弹窗打开 / 保存 / 切换 / 删除"这几个用户动作后整体重建，没有选中项也没有轮询，因此每次重建一批新实例是安全的，不需要按 id 复用。
4. **"编辑"回填的是原始字段，不是 `Title` / `Subtitle`**。那两串是给人看的：`Title` 带"（当前使用）"后缀、`Subtitle` 是 `模型 · URL · Key xxx` 拼接串，回填进去会把数据写坏。ApiKey 一律**不回填**（服务端只下发脱敏串，明文从一开始就不出服务端；若把上一次新增时输入的明文留在框里，用户会以为那就是当前供应商的 Key）。

**几个实现细节（同样有意为之）**：

- **`PasswordBox` 双向同步走 code-behind 事件，不引入附加属性**。`PasswordBox.Password` 不是依赖属性、绑不了，也没有 `UpdateSourceTrigger`。这里用 `PasswordChanged`（View → VM）+ VM `PropertyChanged`（VM → View）+ `_syncingPassword` 重入标志。**必须双向**：`ResetForm()` 与 `BeginEdit()` 都会由 VM 侧主动改这个值，只做单向的话，用户点完"编辑"后框里还留着上一次输入的明文 Key，而 VM 里已经把它当成"没填"。
- **取数放在 `Loaded`，不放构造器**。`LoadAsync()` 是 async，构造器里没法 `await`（`.Result` / `.Wait()` 会锁 UI 线程）。这与 `AutoReplyWindow.OnLoaded` 是同一处理。
- **`ModelSettingsWindow.OnClosed` 里 `Dispose()` 弹窗 VM**。它的生命周期跟随窗口，连带取消在途请求——用户点完"验证 Key"就关窗时，那条最长 180 秒的请求在这里被取消。**与主 VM 不同**：主 VM 由 `App` 持有、比窗口活得久，所以 `MainWindow.OnClosed` 只退订不 `Dispose`。
- **首次启动自动弹窗的信号是「VM 抛事件 → View 用 `Dispatcher.BeginInvoke` 延后开窗」**。对照 app.js 的 `if (!activeProviderId) openModelModal()`。之所以不让 View 自己查"配没配模型"：判据 `active_id` 是 VM 在 `InitializeAsync` 里取回来的，而 View 的 `Loaded` 早于那次请求返回；之所以必须延后：事件是在 `InitializeAsync` 内部同步抛出的，直接 `ShowDialog()` 会把紧随其后的 `StartPolling()` 一起堵到弹窗关闭为止（表现就是"弹窗开着时消息列表、自动回复状态、余额全都不刷新"）。
- **切供应商后必须连余额一起回刷**（`RefreshModelStateAsync` = 重取供应商 + 重查余额）。换一个供应商就是换一个账号的钱，只等 60 秒一轮的 `BalancePollInterval` 自己刷，中间显示的是上一个供应商的余额。**已知取舍**：`RefreshBalanceAsync` 带 `_balanceLoading` 防重入，若此刻正好有一条余额查询在途，这一次会被跳过、界面沿用上一次文案，最迟 60 秒后被轮询纠正；刻意不绕开那道防重入——并发两条查询的返回顺序不定，反而可能用旧供应商的余额覆盖新值。
- **删掉的正是当前编辑对象时，表单退回"新增"**。否则下一次保存会拿着一个不存在的 id 去打 404。
- **`SelectedProviderType` 的 setter 做 null 归一**。`ComboBox` 有一条会"反向写坏数据"的行为：绑定值在 Items 里找不到对应项时会把 `SelectedValue` 回写成 `null`；弹窗初次加载时 `Presets` 还没灌进来、绑定值又是空串，正好命中。`null` 流下去会有两处爆掉：`ApplyPreset` → `Dictionary.TryGetValue(null)` 抛 `ArgumentNullException`；`SaveProviderRequest.ProviderType` 变 null 让服务端退回 `custom`。
- **预设表只在第一次拿到时灌进下拉框**。每次刷新都 `Clear` + 重填会打断当前选中项（`SelectedValue` 先变空再变回），而预设不会在运行期变化。
- **`↳ 遗留项`：与 T10 的 `DangerButton` 重名但定义不同**。T10 把 `DangerButton` 定义在 `AutoReplyWindow.xaml` 的窗口资源里（静止态是浅红填充 `DangerSoftBrush`），本次把它提升进了 `Theme.xaml`（静止态是白底红字 `CardBrush`）。两个同名 key 在不同资源作用域内，`AutoReplyWindow` 里那个局部定义胜出，**运行期不冲突、但两个"危险按钮"外观不一致**。未去改 `AutoReplyWindow.xaml`（T10 改动面），留待 T12 之后统一：把局部那份删掉、改用它自己的语义色或直接复用主题版。

### 7.10 T16 交付记录（导航重构，已完成）

**状态**：`dotnet build` 通过，**0 error / 0 warning**。

| 文件 | 职责 |
|---|---|
| `Views/MainWindow.xaml(.cs)` | 只剩窗口级职责：`56px 图标栏 + 内容宿主` 两列、`DataContext` 挂接、窗口级事件订阅、拦关窗、最小化回写 |
| `Views/Modules/ChatModuleView.xaml(.cs)` | 原第 1/2/3 栏（会话 / 消息+筛选 / AI 分析）+ 导出入口；订阅 `MessagesUpdated` / `AiThreadUpdated`，负责三处滚动 |
| `Views/Modules/ScheduledSendModuleView.xaml(.cs)` | 原第 0 栏（定时发送），含跟随搬过来的 `ThemedCheckBox` 局部样式与任务编辑弹窗 |
| `Views/Modules/SettingsModuleView.xaml(.cs)` | 「模型设置 / 自动回复」两个入口 + 两条状态文案 |
| `Views/Modules/MomentsModuleView.xaml(.cs)`、`ContactsModuleView.xaml(.cs)` | 占位。**这两个文件名就是 T22 / T23 要原地替换内容的文件，不得改名** |
| `Views/Modules/ModuleDialogs.cs` | `internal static`，开弹窗的唯一实现；只开窗与收尾，**不含延后调度** |
| `ViewModels/MainViewModel.cs` | 新增 `MainModule` 枚举 + `CurrentModule` + 5 个 `IsXxxModule` bool 投影 |

**四条必须遵守的约定（后续窗口改这块前先读）**：

1. **模块一律不设 `DataContext`**，靠 `MainWindow` 继承 `MainViewModel`。否则普通绑定会跑偏，且 `{Binding DataContext.XXX, RelativeSource={RelativeSource AncestorType=Window}}`（删除定时任务命令、复制 AI 回答命令，共 2 处）会解析到错误的上下文。
2. **`Visibility` 只能写在 `Style` + `DataTrigger` 里**，不能写成元素上的局部值——局部值优先级高于 Trigger 的 Setter，写了它 Trigger 就永久失效（与 §7.6 坑 2 同源）。
3. **`IsXxxModule` 的 setter 只认 `true`**。`RadioButton` 组内互斥时，点 B 会把 A 的 `IsChecked` 写成 `false` 并沿双向绑定回写；若对 `false` 也做反应，会把 `CurrentModule` 改回上一个值。
4. **VM 事件的订阅方只有一处**：`MessagesUpdated` / `AiThreadUpdated` → `ChatModuleView`（`Loaded` 订阅、`Unloaded` 退订）；`AlertRequested` / `ConfirmRequested` / `ModelSettingsRequested` → `MainWindow`（`MessageBox` 的 owner 必须是窗口）。两处清单必须各自与订阅一一对应。

**两处刻意留空（不是漏做）**：图标栏顶部的账号头像、底部的「主题」按钮只留点击挂点，逻辑分别属 T19 / T18。模块入口用文字而非图形（`Assets/` 下只有 `app.ico`，没有图标资源）。

**遗留（不影响使用）**：
- 定时任务模块右侧详情区只有一行静态说明文案（§14.2.2 定义的"下次触发 / 上次发送汇总"尚无确定形态，未编造绑定）。
- 真机上"切模块是否保留聊天列表滚动位置"未验证（`Visibility` 切换不重建，理论上保留）。

### 7.11 T21 交付记录（后端代理 + 契约落地，已完成）

**状态**：`dotnet build` 通过（0 error / 0 warning）；`import server` 通过（35 条 `/api/*` 路由，其中新增 10 条）；收尾后进程、端口、`.shutdown-requested` 全部干净。

| 文件 | 职责 |
|---|---|
| `app/adapters.py` | 新增 `_post` / `_fetch_bytes` 私有通道，以及 `list_contacts` / `fetch_avatar` / `sns_timeline` / `sns_users` / `sns_sync_latest` / `fetch_sns_media` / `fetch_sns_video` / `sns_export_*` |
| `app/server.py` | 新增 10 条端点（见下）+ `absolute_self_url` / `contact_avatar_path` / `_sns_media_params` / `_sns_export_payload` / `_sns_export_id` / `_export_filename` |
| `Services/ChatlogApiClient.cs` | 新增 §8/§9 调用方法 + `SendBytesAsync` 二进制通道 + 两个长超时常量 |
| `Models/ContactModels.cs` | `ContactCounts` / `ContactSummary` / `ContactListResponse` / `ContactQuery` |
| `Models/MomentsModels.cs` | `SnsUser(s)` / `SnsMedia` / `SnsMediaSize` / `SnsPost` / `SnsTimelineResponse` / `SnsSyncLatest(Response)` / `SnsMediaRequest` / `SnsExportRequest` / `SnsExportResponse` |

**新增的 10 条端点**：`GET /api/contacts`、`GET /api/contacts/avatar`、`GET /api/sns/timeline`、`GET /api/sns/users`、`POST /api/sns/sync-latest`、`POST /api/sns/media`、`GET /api/sns/video`、`POST /api/sns/export`、`GET /api/sns/export/{id}`、`GET /api/sns/export/{id}/download`。

**三条给 T22 / T23 的关键信息**：

1. **契约的权威来源是代码里的 DTO**（`ContactModels.cs` / `MomentsModels.cs`），字段名与 §14.7.1 / §14.8.1 / §14.8.1.1 逐字一致。**不要再回去读文档猜字段**，直接看 DTO。
2. **朋友圈图片必须用 `SnsMediaRequest.ForPostMedia(post, media, index, fullSize)` 构造请求**。它内部做的就是"整套挑一套 attrs"——原图用 `urlAttrs`、缩略图用 `thumbAttrs`；**两者 token 不同，绝不能拼装**。
3. **渲染前先判 `SnsMedia.CanRenderImage`**：实测存在"没有任何定位参数"的 media（视频号那条），这类要退化为外链文本或占位，**九宫格必须容忍空格子**。

**一条实现约束**：`/api/sns/media` **刻意是 POST**。定位参数含 token/key，放 query 会被 uvicorn 的 access log 写进 `logs/chatlog.log`（§10 R10 已把 `logs/` 列为禁止外发）。**不要"顺手简化"成 GET**——`server.py` 里那段 docstring 已写明理由。

**已知限制**：`likes` / `comments` / `finderFeed` / `official` / `cover` / `covers` 以及导出任务对象**结构未实测**，后端原样透传、客户端用 `JsonElement` 兜住，界面按需解析。这是刻意的：不猜结构、不编造字段。

**后续修正（2026-09-11）**：`sns_sync_latest` 的成功判据漏掉了上游的 `noop` 幂等状态，「无新增」被当成失败。已按端点放行修复，约束与实测 payload 见 §14.8.1 的「`sync_latest` 的一个实测陷阱」。

### 7.12 T22 交付记录（联系人模块，已完成）

**状态**：`dotnet build` 0 error / 0 warning；**真机 6 项点检全部通过**（列表有数据、勾选/全选/反选计数正确、跨筛选保留已选、CSV 用 Excel 打开中文不乱码、`sourceFallback` 横幅正常显示、点行=勾选联动）。

| 文件 | 职责 |
|---|---|
| `Views/Modules/ContactsModuleView.xaml(.cs)` | 两列（左=筛选条+工具栏+横幅+ListBox，右=详情区）；不设 `DataContext`；`IsVisibleChanged` 首次可见触发加载；回车=应用；`SaveFilePicker`/`AlertRequested` 的 View 侧实现 |
| `ViewModels/ContactsModuleViewModel.cs` | 筛选 → `ContactQuery`、`EnsureLoadedAsync`、跨筛选已选集合、全选/反选、计数、详情头像懒加载、导出编排 |
| `ViewModels/ContactItemViewModel.cs` | 列表行 VM（勾选态、首字母头像、显示名口径、徽标、详情字段） |
| `Services/ContactExporter.cs` | 无状态静态格式化器，复用 `ExportFormat` / `ExportDocument` |
| `ViewModels/MainViewModel.cs` | 仅新增 `Contacts` 属性（构造时只 `new`、不拉数据） |

**四条后续窗口要沿用的做法（T23 直接照抄这一套，别另立一套）**：

1. **已选集合与"当前可见列表"完全解耦**：`_selected`(HashSet) 管成员、`_selectedOrder`(List) 管导出顺序、`_known`(只增不减) 管跨筛选取数据；「全选」是**并入**当前结果而非替换。
2. **"只加载一次"的标记只在成功之后置位**（`_loadedOnce`），失败必须允许重试——否则首次碰到 503 就永久救不回来。
3. **失败不清空列表**：一次网络抖动不该让用户看到"数据全没了"。
4. **详情区头像懒加载**：列表 495 行逐条请求头像是无谓压力；只有真正要看的那一个才下载，并**取消上一次在途请求**（否则后到的旧响应会盖掉用户新选中的那个）。

**关于头像导出的一处刻意取舍**：导出文件里**不含任何头像字段**（`Avatar` 是本机代理地址、外发必成死链；`AvatarLink` 是长 URL、在表格里是噪声）。将来若要带头像，**只允许用 `AvatarLink`**。

**遗留**：`ContactsModuleViewModel.Dispose()` 暂无调用方（`MainViewModel.Dispose` 里补一行即可）。影响极小（仅少 Cancel 一个令牌），已交由 T23 窗口在该文件里顺手补上——因为 `MainViewModel.cs` 是两者共用的文件，避免两个窗口先后保存互相覆盖。

### 7.13 T23 交付记录（朋友圈模块，已完成）

**状态**：`dotnet build` 0 error / 0 warning；**真机 8 项点检全部通过**（进模块自动同步且文案带扫描/新增数、卡片渲染、九宫格取图、滚动多屏无"永久加载中"、无缩略图格子按预期退化、大图开关与切模块自动关闭、滚动翻页去重、导出 ZIP 可打开）。

| 文件 | 职责 |
|---|---|
| `Views/Modules/MomentsModuleView.xaml(.cs)` | 左 240 筛选栏 + 右 ListBox 动态流 + 模块内大图遮罩；`Loaded` 触发格子取图；`IsVisibleChanged` 触发加载与关遮罩 |
| `ViewModels/MomentsModuleViewModel.cs` | 分页/去重（`_seenPostKeys`）、同步、筛选、遮罩状态、导出窗工厂；内含 `MomentUserOption` |
| `ViewModels/MomentItemViewModel.cs` | 卡片 VM：展示字段构造时算完 + `likes`/`comments` 试探解析与计数降级 |
| `ViewModels/MomentMediaViewModel.cs` | 格子 VM（状态机 + 尺寸）+ 同文件内 `MomentMediaLoader`（并发 4 / 缓存 / `OnLoad`+`Freeze`） |
| `ViewModels/MomentsExportViewModel.cs` + `Views/MomentsExportWindow.xaml(.cs)` | 导出弹窗：格式/范围（未提交编辑态）、每秒轮询、下载、另存为编排 |
| `ViewModels/MainViewModel.cs` | 仅三处：`Moments` 属性 / 构造器 1 行 `new` / `Dispose` 补两行 |
| `app/adapters.py` | **一次性授权改动**：`_post` 增 `ok_statuses`、`sns_sync_latest` 放行 `noop`、错误消息不再 dump 整段 payload |

**五条设计约定（改这块前先读）**：

1. **取图一律经 `SnsMediaRequest.ForPostMedia`**，格子 VM 不碰 attrs 里的任何字段（§14.8.1.1：原图与缩略图是两个不同的 token）。
2. **请求触发点是"格子的 UI 元素进入可视区"（`Loaded`）**，不是 VM 构造 —— 条目 VM 整批创建，若在建 VM 时就发请求，一页 180 张图会同时涌向后端。配合 `MomentMediaLoader.MaxConcurrency = 4` 排队。
3. **不做"滚出视野即取消"**（收益小、写漏状态复位就是永久空白格）；取消只发生在模块退出时。但 `ResetToNotLoaded()` 必须保留并在取消路径上调用。
4. **`MomentMediaLoader.Dispose` 刻意不释放 `SemaphoreSlim`**：在途请求的 `finally` 还会 `Release()` 一次，晚于 Dispose 就抛 `ObjectDisposedException`，而它发生在一个 `_ = ...` 的任务里没人看得见。**不要"顺手"把它补上。**
5. **格子状态用状态机**（`NotLoaded / Loading / Loaded / Failed / Unavailable`）而不是几个独立 bool：失败、取消、无参数三条路径必须可区分。缓存键 `post_id|media_id|variant`，`media_id` 为空时用下标兜底（实测视频号那条 `media.id` 就是空的）。

**顺带修掉的一个后端 bug（2026-09-11）**：`sns_sync_latest` 把上游的 `noop`（幂等无操作）当失败抛，界面显示"同步失败：{...整段 payload}"。已按端点放行并顺手修掉"错误消息 dump 整段 payload"的问题，约束与实测 payload 见 §14.8.1 的「`sync_latest` 的一个实测陷阱」。

**已知限制**：本轮**不做"取消导出任务"**（后端只代理了 create / status / download，未暴露上游的 `DELETE`，见 §14.9）；`likes` / `comments` / `finderFeed` / `official` 等未实测结构只做计数或文本级降级，不猜字段；不做视频播放。

### 7.14 T17 + T18 交付记录（免责声明 + 浅色/深色主题，已完成）

**状态**：`dotnet build` 0 error / 0 warning；真机人工查看通过（用户确认）。
**范围核对**：只改了 `App.xaml(.cs)` / `Themes/*` / `Services/ClientSettings.cs` / `Views/DisclaimerWindow.*` / `Views/MainWindow.*` / `Views/Modules/SettingsModuleView.*`；**`ViewModels/MainViewModel.cs` 未被碰**（时间戳停在 T23 那次编辑）；`MarkdownRenderer.cs` 按"能不改就不改"确认未改。

| 文件 | 职责 |
|---|---|
| `Themes/Theme.Light.xaml` / `Theme.Dark.xaml` | **只放 32 个颜色 Brush**。两份 key 逐项一致（已核对：数量、顺序、分节完全相同） |
| `Themes/Theme.xaml` | **只剩转换器 + 控件样式**（第 8~14 节），已无任何 `SolidColorBrush` 与字面量色值；控件模板仍只有一份 |
| `App.xaml(.cs)` | `MergedDictionaries` = [当前主题颜色字典] + `Theme.xaml`；`ApplyTheme` 按键**替换**字典项（不 `Clear`）；启动时按偏好挂 |
| `Services/ClientSettings.cs` | 客户端本地设置（`client-settings.json`，放 AppPaths 数据目录）：`Theme` / `SuppressDisclaimer`；读写失败静默回退默认值 |
| `Views/DisclaimerWindow.xaml(.cs)` | 自定义模态窗口，四段文案 + 「不再提示」勾选；主窗口首帧后排 `Background` 优先级弹出，**先判 `SuppressDisclaimer` 再决定是否建窗** |
| `Views/Modules/SettingsModuleView.xaml(.cs)` | 新增「重新显示免责声明」入口（只新增，未重排既有内容） |
| `Views/MainWindow.xaml(.cs)` | 只动图标栏那个「主题」按钮：`Content` 显示**当前**主题、tooltip 说明点击后的结果；启动时与点击后各设一次 |

**一条必须守住的约定（T18 的拆法）**：**色值与样式分离**——颜色只在 `Theme.Light.xaml` / `Theme.Dark.xaml`；控件模板**一份**、留在 `Theme.xaml`，对颜色一律 `DynamicResource`。
**不要把整份 `Theme.xaml` 复制成两份**：那会让以后改一个模板要改两处，必然漂移。

**免责声明文案口径**：按用户原文逐字实现，**只把错字「严谨」改成「严禁」**；另两处建议（"违则"→"违者"、"不会泄漏任何数据"补边界）**按原文保留**，等用户明确后再改（代码注释已注明）。

**深色主题的三个"极性冲突"（实测记录，别再重新推一遍）**：

1. `AccentSoftBrush`（选中行底色）在浅色下是"极浅绿 + 深字"，深色下必须反过来成"深绿底 + 浅字"，否则压在它上面的近白色 `TextPrimaryBrush` 会彻底消失。
2. `AccentFillBrush` 在现有样式里被当成**两种角色**：填充底色（要够暗才配白字）＋ 浅绿底上的**文字色**（要够亮）。两个要求方向相反，在固定 key 集合下**数学上无法同时满足**。取值策略：优先保住实心主按钮（白字 ≈ 4.7:1），代价见下方"已知缺口"。
3. `TextOnAccentBrush` 必须保持白色：它还被朋友圈大图查看器用作遮罩上的说明文字。

**★ 已知缺口（T24）**：深色下"`AccentFillBrush` 当文字色压在 `AccentSoftBrush` 上"约 **3.3:1**（低于小字号 AA 4.5:1，高于大字号 3:1），共 **5 处**（选中态标题、预设按钮文字等）。
**根治办法**：新增一个"浅绿底上的文字色" key（如 `AccentOnSoftBrush`），在两套字典里各给一次取值，然后改那 5 个用点（涉及 `Theme.xaml` 的样式 + `MainWindow` / `ChatModuleView` / `MomentsModuleView` / `AutoReplyWindow` 的 XAML）。属跨文件样式改动，**单独做**，不要塞进功能任务里。

**✅ T24 已修复（2026-09-11）**：新增 **`AccentTextBrush`**（"强调色的文字用法"专用 key），两套主题各一处定义，改掉全部 5 个用点。

| 项 | 结果 |
|---|---|
| 浅色取值 | `#0A7F45`（= 浅色主题的 `AccentFillBrush`）→ **浅色模式视觉零变化** |
| 深色取值 | `#3FD07A`（复用本主题已验证的 `LinkBrush`）→ 压在 `AccentSoftBrush` 上约 **7.7:1**（原 3.3:1），AA 达标 |
| 5 个用点 | `MainWindow.xaml:53`（选中态模板）、`AutoReplyWindow.xaml:160`（群标签）、`AccountWindow.xaml:68`（状态文案）、`AccountWindow.xaml:115`（「当前账号」徽标）、`MomentsModuleView.xaml:212`（点赞行） |
| **为什么最终叫 `AccentTextBrush` 而不是 `AccentOnSoftBrush`** | 第 5 个用点（朋友圈点赞行）的底是 `PanelBrush`，**不是**浅绿底 → 说明这个 key 的真实角色是"强调色的**文字**用法"，不是"浅绿底专用"。名字写错就是永久的知识债，趁只有 5 个用点改最便宜 |
| 验证 | `dotnet build` 0 error / 0 warning；两套主题 key 集合 **33 = 33 零差异**；旧 key 名残留 **0**；`AccentFillBrush` 当 `Foreground` 的残留 **0**；`AccentTextBrush` 用点**正好 5** |

**为什么这 5 处改过去都安全**：它们全是"深色底 + 绿字"的组合——深色下换成更亮的绿是**严格变好**（底越暗越明显），浅色下取值不变是**零变化**。不存在"改好一处、改坏另一处"。

**一个必须记住的验证要点**：**`DynamicResource` 找不到 key 时不会编译报错**，运行期只是该属性取不到值（静默退化）。所以"构建通过"**不足以证明**这类改动是对的——必须另核"key 在两套主题里都存在 + 无残留旧引用"。本次正是靠这一步才发现漏掉的第 5 处（上一轮用 `head_limit` 截断的 grep 把它漏了）。

### 与原前端的对应关系（T8-T10 参考）

原前端：[index.html](file:///d:/code/chatlog-ai-portable/app/static/index.html) / [style.css](file:///d:/code/chatlog-ai-portable/app/static/style.css) / [app.js](file:///d:/code/chatlog-ai-portable/app/static/app.js)（891 行、59 个顶层函数）。功能域划分见 `docs/PROJECT_SCAN.md` §6.5。

**必须一并迁移的行为**（易漏）：

| 行为 | 原实现 | 备注 |
|---|---|---|
| 四组轮询 | 10s 会话 / 5s 消息 / 3s 自动回复状态 / 60s 余额 | 页面隐藏时跳过消息轮询 |
| 时间+条数双重筛选 | 前端 `applyFilters` 再过滤一次 | 后端也支持，但前端是主路径 |
| 编辑供应商时 api_key 留空 = 沿用旧值 | [server.py L655-L657](file:///d:/code/chatlog-ai-portable/app/server.py#L655-L657) | **不要误传空串**，会 400 |
| 守护对象上限 | 8 个好友 + 3 个群聊 | 超限后端 400 |
| 无生效供应商时自动弹模型设置 | `window.onload` | |
| 退出确认 | `/api/shutdown` 会整体退出 | 界面需二次确认 |
| Markdown 渲染 | 前端 `renderMarkdown` | WPF 侧需引入渲染方案（如 Markdig） |
| 时间戳秒/毫秒兼容 | `message_epoch` | `>100000000000` 视为毫秒 |

---

## 8. 导出聊天记录规格（D6 / D7）

| 项 | 规格 |
|---|---|
| 范围 | **仅当前会话**（UI 与底层接口按「多会话」预留，便于后续扩展） |
| 时间筛选 | 复用界面上的时间范围条件（不限制 / 最近 N 分钟·小时·天） |
| 文字格式 | 每行 `<[时间] 发送者: 正文>` |
| 非文本消息 | 沿用适配器的标签（`[图片]`/`[语音]`…），不做 OCR / 语音识别 |
| 含 AI 历史 | **可选**（勾选项）；勾选时把 `/api/history` 返回的 AI 对话追加在末尾 |
| 格式：Markdown | 标题（会话名）+ 时间线列表；AI 历史单独一节 |
| 格式：HTML | 单文件、内联样式，可直接浏览器打开 / Ctrl+P 存 PDF |
| 格式：JSON | 结构化：`{chat, exported_at, range, messages[], ai_history[]?}` |
| 格式：CSV | 列：`时间, 发送者, 类型, 内容`，UTF-8 **带 BOM**（否则 Excel 中文乱码） |
| 落盘位置 | 由用户选择（默认「文档」目录），文件名 `<会话名>_<时间戳>.<ext>` |

---

## 9. 图标替换方法

占位图标位于：`client\ChatlogAI.Client\Assets\app.ico`

**引用它的地方只有两处**（都指向同一个文件）：

1. `client\ChatlogAI.Client\ChatlogAI.Client.csproj` 的 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` → 决定 exe 自身的图标
2. `client\installer\ChatlogAI.iss` 的快捷方式 `IconFilename` → 决定桌面/开始菜单快捷方式图标

**替换步骤**：

1. 用同名文件覆盖 `Assets\app.ico`。
2. 重新生成客户端（`dotnet build`），若图标没变，是因为 Windows 图标缓存——把 exe 复制到别处或改个名即可看到新图标。
3. 重新编译安装包。
4. 若想换其它文件名/格式，改上述两处引用即可。

**ICO 建议包含尺寸**：`16 / 24 / 32 / 48 / 64 / 256`（256 用 PNG 压缩的内嵌格式）。缺 256 会导致大图标视图模糊。

---

## 10. 风险清单

| # | 风险 | 说明与对策 |
|---|---|---|
| R1 | **Job Object 归属** | 原方案靠 Job Object 保证"关窗口即全死"。.NET 需 P/Invoke `CreateJobObject`/`SetInformationJobObject`/`AssignProcessToJobObject`；漏做会出现残留进程占端口，下次启动报"端口被占用" |
| R2 | **broker 未纳入 Job** | 原实现就漏了（[L291-L300](file:///d:/code/chatlog-ai-portable/app/launcher.py#L291-L300)），仅靠 `--parent-pid` 兜底。.NET 版应补齐 |
| R3 | **每次启动弹 UAC** | D4 的已知代价。若后续要消除，需改「计划任务」或「拆分提权进程」方案，属独立议题 |
| R4 | **Program Files 不可写** | 必须完成 T2（`CHATLOG_DATA_DIR`），否则 `config/`、`data/` 写入失败。**这是 T2 存在的唯一原因** |
| R5 | **旧数据迁移** | 便携版用户的 `config/api-keys.json`（含真实 Key）、`data/summaries.json` 在项目根目录。首次安装需搬迁到 ProgramData，否则用户"配置全没了" |
| R6 | **`/api/shutdown` 语义** | 后端 `os._exit(0)` 会杀掉后端进程；.NET 需监听并同步退出整个进程组，不能让客户端以为"服务卡住了" |
| R7 | **Markdown 渲染** | 原前端自带 `renderMarkdown`，WPF 需选型（Markdig 转 FlowDocument 或 WebView2 局部渲染），否则 AI 输出是一坨纯文本 |
| R8 | **内联事件与静态资源** | ~~`app/static` 在 .NET 版里仍被 `server.py` mount（`STATIC_DIR`），不要删除该目录~~ **已于 2026-09-11 推翻并执行删除**：网页版前端与 `launcher.py` 一起退役（T14 提前完成），`server.py` 的 `STATIC_DIR` / `StaticFiles` mount / `index.html` 分支与 `launcher.py` 的"自动开浏览器"一并移除，`/` 只保留一句 JSON 横幅。详见 §7.18 |
| R9 | **上游版本敏感** | 微信解析器清单有效期；不可静默绕过授权 |
| R10 | **敏感文件外发** | `config/api-keys.json` 含明文 Key，`data/` 含密钥与聊天缓存；`.gitignore` 与打包含密钥文件时务必复核 |
| R11 | **首次授权必须管理员** | 原逻辑：未授权且非管理员直接退出。.NET 版应在启动最早期判定并给出可读提示，不要表现为"点了没反应" |

---

## 11. 需改动的既有文件（逐条说明理由与风险）

### 11.1 `app/server.py`（唯一必须改的 Python 文件）

**改动**：新增可写数据根目录 `DATA_ROOT`，把 `CONFIG_DIR` / `API_KEYS_FILE` / `PROMPTS_DIR` / `AUTO_REPLY_DIR` / `SUMMARIES_FILE` 从 `ROOT_DIR` 改挂到 `DATA_ROOT`；`STATIC_DIR` 保持挂 `ROOT_DIR`。

**理由**：R4。程序装 Program Files 不可写，数据必须落在 ProgramData。

**向后兼容**：`DATA_ROOT = os.environ.get("CHATLOG_DATA_DIR") or ROOT_DIR`。便携模式（不设该变量）行为与今天完全一致。

**风险**：低。仅为路径根变量替换，不涉及业务逻辑。

### 11.2 退役项（T14，暂不执行）

`app/launcher.py`、`启动Chatlog-AI.bat`、`首次初始化.bat`：.NET 版验证通过后再移除，当前保留作为回滚路径。

**`app/static/`（index.html / style.css / app.js）：已明确暂时保留，等 .NET 版整体验证通过（T12 / T13 之后）再删。**
**（原"前置依赖 T9 / T10"已解除：T9 / T10 均已落地，见 §7.9 / §7.8，.NET 客户端现在也能配模型、切供应商、设置自动回复了。）**
当前仍不删的理由只剩"回滚路径"：`app/static/` 是这条链路上最直观的对照实现，删掉后一旦 .NET 版有行为分歧，就没有可逐函数比对的基准。

> 更正一条早先的误判：曾担心删掉该目录会让后端起不来。实测不成立——`server.py` L1034 有 `if STATIC_DIR.exists():` 守卫，L1040 也判断了 `index.html` 是否存在，缺失时 `/` 退化成一句 JSON。删目录本身不会报错。

---

## 12. 新窗口续做指引

1. 先读 `docs/PROJECT_SCAN.md`（改造前现状）与 `docs/API_REFERENCE.md`（后端接口），再读本文件。
2. 看 §7 任务表确认进度，从第一个 `[ ]` 继续。
   - **§7 里有两批队列**：T12–T14 是第一批的收尾（安装包 / 数据迁移 / 退役网页版），T16–T23 是 2026-09-11 提出的第二批（导航重构 + 7 项新功能），规划见 **§14**。
   - **§14.10 的两项取证与六项决策已于 2026-09-11 全部闭环**，可以开工。接口路径与字段一律以 §14.7.1 / §14.8.1 的**实测结论**为准，**不要再用 §14.7 / §14.8 早期版本的逆向结论**（联系人路径曾误记为 `/api/contacts`）。
3. 施工环境提醒：`dotnet` 用全路径 `C:\Program Files\dotnet\dotnet.exe`；**构建前必须先做 §13.1 的环境变量重定向**，否则沙箱会拦 NuGet。
4. **每完成一个任务都要回来更新 §7 的状态与备注**。

---

## 13. 构建与运行命令

### 13.1 ★ 构建前置（在 Trae 沙箱内必须做，否则必失败）

沙箱禁止读写 `%APPDATA%\NuGet`，`dotnet build` 会报
`由于未授权访问权限，因此未能读取 NuGet.Config`。必须先重定向这两个环境变量：

```powershell
$env:APPDATA = 'C:\Users\djct\AppData\Local\Temp\trae-appdata'
$env:NUGET_PACKAGES = 'C:\Users\djct\AppData\Local\Temp\trae-nuget-packages'
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES | Out-Null
```

### 13.2 编译客户端（Debug）

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "d:\code\chatlog-ai-portable\client\ChatlogAI.sln" -c Debug
```

### 13.3 发布（Release，供安装包使用）

```powershell
& "C:\Program Files\dotnet\dotnet.exe" publish "d:\code\chatlog-ai-portable\client\ChatlogAI.Client\ChatlogAI.Client.csproj" -c Release -r win-x64 --self-contained false -o "d:\code\chatlog-ai-portable\client\out"
```

### 13.5 本地联调：起停上游的**提权**要求（2026-09-11 实测踩坑）

上游 `wechat-backend.exe` 启动后会**自我提权**派生真正的后端与 broker 进程。

后果：**用非管理员窗口起的上游，非管理员窗口杀不掉**——`Stop-Process -Force` 与 `taskkill /F` 都返回"拒绝访问"，连进程的 `Path` 都读不到（权限不足），**端口 10392 会一直被占**，下一个窗口/下一次启动就会报"端口被占用"。实测踩到过一次（`IsInRole(Administrator)` 为 `False` 的窗口起了上游，收尾时 4 个进程全部杀不掉）。

**规则**：本地联调要起上游时，**必须在管理员终端里起、并在同一个管理员终端里收尾**；或者用任务管理器结束（会提示需要管理员权限）。

收尾校验（必须在管理员终端执行，两条输出都要是 `0`）：

```powershell
Get-Process wechat-backend,wechatdb_broker -ErrorAction SilentlyContinue | Stop-Process -Force
(Get-NetTCPConnection -LocalPort 10392 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
```

> 反之，**本项目自己的后端（`python app/server.py`，18765 端口）不需要提权**，非管理员窗口起停都没问题。

### 13.4 开发期直接运行客户端（不安装、不提权环境）

用两个环境变量把根目录指向仓库，即可在 `bin\Debug` 里直接跑（见 §7.1）：

```powershell
$env:CHATLOG_ROOT_DIR = 'd:\code\chatlog-ai-portable'
$env:CHATLOG_DATA_DIR = 'd:\code\chatlog-ai-portable\client\out-data'
& "d:\code\chatlog-ai-portable\client\ChatlogAI.Client\bin\Debug\net8.0-windows\ChatlogAI.exe"
```

---

## 14. 第二批需求规划（2026-09-11：导航重构 + 7 项新功能）

> **本章是规划，不是完成记录**。进度看 §7 的 T16–T23。
> **目标端只有 .NET 客户端**：`app/static/` 网页版不跟进（T14 后即退役）；`app/server.py` 新增的接口两边都能调，但只按 .NET 侧验收。

### 14.1 需求与已确认结论

| # | 需求（用户原话摘要） | 结论 |
|---|---|---|
| 1 | 尝试自动识别用户快捷键，识别不到再退到用户自己配置 | 见 §14.3。**已真机取证：微信 4.1 读不到热键 → 本需求降级**为"手工配置 + 一键恢复默认值"，保留探测骨架 |
| 2 | 提供按钮快捷切换微信（如果有） | 见 §14.4。用户补充：「切换账号」与「唤起微信窗口」**都要**，且切换要处理「目标账号未解析」与「提醒用户把微信登录切回去」两种情况 |
| 3 | 启动弹免责声明提示框 | 见 §14.5。每次启动都弹 |
| 4 | 提供按钮切换主题颜色 | 已确认：**浅色 / 深色两套**，见 §14.6 |
| 5 | 联系人信息（多选 / 全选）一键导出 | 已确认：数据源用**上游通讯录接口**；但**多选只能由我方自己格式化导出**（已实测上游导出接口不支持按 username 列表导出），见 §14.7 |
| 6 | 新增微信朋友圈查看 | 见 §14.8 |
| 7 | 最左侧图标栏增加「朋友圈查看、导出」入口 | 与 §14.2 导航重构同批做；已确认**朋友圈不做 AI 总结** |

---

### 14.2 UI 诊断与目标交互模型（T16）

#### 14.2.1 现状为什么不对（三条，都不是审美问题）

1. **低频功能常驻一整列。** 「定时发送」是"配一次、之后几周不动"的功能，却永久霸占最左 232px（[MainWindow.xaml L97-L257](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/MainWindow.xaml#L97-L257)），和"看聊天记录"这条主流程抢空间。它在微信里也没有对应的心智模型，用户第一眼不知道这栏是干什么的。
2. **配置入口在语义上无处安放。** 模型 / 自动回复 / 导出被塞在定时发送栏底部（[L226-L255](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/MainWindow.xaml#L226-L255)）。它既不属于"定时发送"，也不属于任何模块——这正是需求 7 说的"不应该放在那里"。
3. **没有"模块 / 视图"这一层抽象。** 每加一个功能只能"再加一列"，于是 `MinWidth` 一路涨到 1490（[L6](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/MainWindow.xaml#L6)）。微信不是这么组织的：它用**左侧图标栏切换主面板**，内容区按模块整体换掉。

#### 14.2.2 目标模型：图标栏 + 列表栏 + 详情区

```
[ 图标栏 56 ] [ 列表栏 232~240 ] [            详情区（跟随模块）           ]
```

| 图标栏项 | 列表栏内容 | 详情区 |
|---|---|---|
| 顶部：账号头像（点击 → 账号菜单，需求 2） | — | — |
| 聊天（默认） | 会话列表（现状不动） | 消息 + 筛选 + AI 分析（现状两栏，原样搬入） |
| 定时任务 | 任务列表（现有面板整体搬入） | 保留现有编辑弹窗；可另显示下次触发 / 上次发送汇总 |
| 朋友圈（需求 6/7） | 左：筛选栏（按人 / 关键词 / 「同步最新」） | 右：**动态流**（单列纵向滚动，每张卡片自含文字 + 九宫格 + 点赞评论）；**无 AI 分析区** |
| 联系人（需求 5） | 联系人列表（多选 + 全选 + 导出按钮） | 联系人详情（备注 / 昵称 / 微信号 / 地区 / 来源） |
| 底部：主题切换（需求 4） | — | — |
| 底部：设置 | 设置项导航 | 模型 / 自动回复 / 快捷键（需求 1）/ 免责声明 / 关于 |
| 底部：退出 | — | — |

「导出」**不单独设图标项**，而是做成**当前模块的工具栏动作**（会话里导出会话、联系人里导出联系人、朋友圈里导出朋友圈）。理由：导出永远需要一个"导什么"的上下文，脱离上下文的"导出"按钮必然还要再问一次"导哪个"。需求 7 只说左栏"可增加"，按此处理；若你坚持要独立入口，代价是要在导出窗口里再加一层"数据源选择"。

#### 14.2.3 三条实现约束（比"控件怎么摆"更关键）

1. **必须拆成独立 `UserControl`。** ~~不做「一个大 Grid + Visibility 全切换」~~ → **已落地（T16），并更正一处过宽的约束**：当初要禁止的是"把模块内容内联在一个文件里"（`MainWindow.xaml` 已 890 行），**不是"Visibility 切换"这个机制本身**。模块独立成 `UserControl` 后该问题已不存在，所以最终采用的就是"5 个常驻实例 + `Visibility` 切换"——它比 `ContentControl` 换 `Content` 更可预测（换 `Content` 会让元素离树再入树，`ScrollViewer` 的 offset 不保证恢复）。
   实际落地：`Views/Modules/` 下的 `ChatModuleView` / `ScheduledSendModuleView` / `MomentsModuleView` / `ContactsModuleView` / `SettingsModuleView`，外加共用的 `ModuleDialogs.cs`（开弹窗的唯一实现）；`MainWindow` 只留"图标栏 + 内容宿主"两列。详见 §7.10。
2. **图标栏用互斥单选语义**（`RadioButton` + `GroupName`，或自绘 `ToggleButton` 手动互斥），**不要用 `ListBox` 假装**。它表达的是"当前是哪个视图"，不是"选中了哪一行数据"；用 `ListBox` 还得额外处理键盘导航、选中高亮与假选中。
3. **`MinWidth` 要重算，而且是变小。** 重构后聊天模块 = 56 + 240 + 消息 + 360，定时发送模块 = 56 + 232 + 详情，最宽的一屏也不再是"四栏并列"。**不要沿用 1490**，否则窗口最小尺寸凭空多出 56px 的图标栏。

---

### 14.3 需求 1：快捷键自动识别（T20）

#### 14.3.0 真机取证结论（2026-09-11，**决定性**）

**结论：在微信 4.1 上"自动识别用户快捷键"没有可行路径，本需求按降级方案落地。**

取证过程与证据（本机，微信 4.1.11.55 / 4.1.12.55）：

| # | 探测点 | 结果 |
|---|---|---|
| 1 | `HKCU\Software\Tencent\WeChat` | 键存在，但**没有任何子键、没有任何值** |
| 2 | `HKCU\Software\Tencent\Weixin`（4.x 的新键） | 同上：空键，无值 |
| 3 | 在 `HKCU\Software\Tencent` 下递归 5 层，找值名含 `hotkey / HotKey / shortcut / 快捷键` | **零命中** |
| 4 | `HKLM\SOFTWARE\Tencent` | 不存在 |
| 5 | `%APPDATA%\Tencent\xwechat\config\*.ini` | 只有 19 字节，内容是数据根路径，与热键无关 |
| 6 | 账号级配置 `xwechat_files\<wxid>\config\`：`newclientconfig\config`(36 KB)、`rs_config`(4 KB) | **加密二进制**（高熵、无任何可读字符串） |
| 7 | `login_config` / `all_users\config\global_config` / `client_config` | 被运行中的微信进程**独占锁定**，且为二进制定长文件 |
| 8 | 在上述 `config*` 目录内全文搜 `hotkey / shortcut / 快捷键` | 只命中 Chromium/Electron 运行时噪音（liteapp 缓存、locale `.pak`、`libvlccore.dll`），**没有一处是微信自身的设置** |

项目里唯一"能读到微信本机设置"的链路是 `account_keys.json`（解密密钥），与快捷键无关；要读出热键只能去解密第 6 项那种加密配置——既不可靠（随版本变）也不该做（把解密能力用在非必要的地方）。

**顺带更正一处既有记录**：§7.4 写"本机微信是 4.0"，实测安装目录 `D:\app\wechat\Weixin` 下是 **4.1.11.55 与 4.1.12.55**。不影响任何既有结论，只影响以后按版本号查资料时的判断。

#### 14.3.1 现状事实（已核实，全仓库）

- 快捷键的**使用**在 [automation.py](file:///d:/code/chatlog-ai-portable/app/automation.py#L180-L195)：`"+"` 分段 → `_MODIFIERS` 与 `VK_MAP` 映射 → `SendInput`。支持 `ctrl/alt/shift/win` 任意组合与单键（`enter` 等）。
- 快捷键的**读取**：**不存在**。全仓库（含 `runtime/`）检索 `winreg / RegOpenKey / UIAutomation / 微信配置文件解析`，业务代码零命中——项目只有"盲发按键"的能力，没有任何"读回微信设置"的通道。
- 现有的"测试"按钮（[AutoReplyWindow.xaml L244-L330](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/AutoReplyWindow.xaml#L244-L330)）只是把某个 action 的按键**盲发一次**，不做任何反馈校验，**无法回答"这个键在微信里到底对不对"**。这正是本需求想补的缺口。
- 默认值：`open_wechat=ctrl+alt+w`、`search=ctrl+f`、`paste=ctrl+v`、`send=enter`（[AutoReplyViewModel.cs L38-L41](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/ViewModels/AutoReplyViewModel.cs#L38-L41)）。

#### 14.3.2 落地形态：降级方案（不是"能识别就识别"）

取证结论出来后，原先设想的三个探测源（注册表 / 本地配置 / 上游接口）**全部被证伪**，因此本需求落地为下面四项，**不再对外承诺"自动识别"**：

| # | 交付内容 | 说明 |
|---|---|---|
| 1 | 保留「手工配置 4 项快捷键」 | 现有能力，不动 |
| 2 | 新增「恢复默认值」按钮 | 用户改乱之后一键回到 `ctrl+alt+w / ctrl+f / ctrl+v / enter` |
| 3 | 新增提示区 | 不弹错误框，只显示一行：`未能自动识别（微信 4.1 未提供可读的快捷键设置），请手工填写微信「设置 → 快捷键」中的值` |
| 4 | 保留探测骨架（**当前为空实现**） | 接口仍然存在，实现为"依次尝试 N 个探测源 → 全部失败返回空"。将来若某版本微信把热键写回注册表，只需补一个探测器，**接口与 UI 都不用改** |

**为什么不干脆把接口删掉**：删了之后，将来微信改回注册表存储时要重新设计接口、重新改 UI；留一个"永远返回空"的实现成本几乎为零（一个 `return`），却把扩展点保住了。

**为什么不做"识别结果直接生效"**：识别错了最坏是"填了个错的默认值"，用户看得见、能改；如果识别结果直接落盘，用户会以为程序"知道"他的设置，错了也不会去查。

#### 14.3.3 明确排除：探测式"试按键"

逐个候选键发送、观察微信反应——**这条路必须排除**，理由按危险度排序：

1. `send` 默认是 `enter`，一旦命中就是**真实发送一条消息**；
2. `paste` 会把内容写进输入框；
3. `open_wechat` 是"显示 / 隐藏"toggle，连按两次状态不可预期；
4. 项目已有前车之鉴：键盘模拟是独占资源，误触发会**把消息发到另一个会话**（[PROJECT_SCAN §8.5](file:///d:/code/chatlog-ai-portable/docs/PROJECT_SCAN.md#L368)）。

#### 14.3.4 顺带修掉的一个盲区（**已确认：做**）

[automation.py L322](file:///d:/code/chatlog-ai-portable/app/automation.py#L322) 里"搜索后按回车进入会话"用的是**写死的 `enter`**，不在那 4 个可配项里——用户在微信里把"打开选中会话"改成别的键，这条链路就废了。

**已确认的处理**：把它提升为**第 5 个可配项**（键名建议 `confirm`），同时做旧配置兼容——配置里没有该键时用 `enter` 兜底，因此**现有用户升级后行为完全不变**。

> 这一条现在比原设想更重要：既然"自动识别"降级了（§14.3.0），用户对快捷键的**手工可配性**就是唯一的调节手段，这第 5 个键不能漏。

#### 14.3.5 接口与 UI 草案

```
POST /api/auto-reply/detect-hotkeys
→ { "status": "success", "found": false, "source": "", "hotkeys": {}, "message": "未能自动识别（微信 4.1 未提供可读的快捷键设置）" }
```

`found` 当前恒为 `false`（§14.3.0）；字段结构保留，是为了将来能直接接上探测源。

UI：自动回复弹窗的快捷键区加两个按钮——「自动识别」（点了只会显示上面那行提示）与「恢复默认值」；识别不到**不报错、不弹框**，只是一行提示。第 5 个键（`confirm`）与现有 4 个并排，同样带「测试」按钮。

#### 14.3.6 风险

| # | 风险 | 对策 |
|---|---|---|
| 1 | ~~微信是否把热键写注册表未经证实~~ | **已取证：读不到**（§14.3.0），已按降级方案落地 |
| 2 | 读到了但解析错（编码 / 键位布局不同） | 结果只填表单、不自动生效——这正是当前设计的默认姿态 |
| 3 | 微信升级后位置变化 | 探测函数按"多源依次尝试 + 全部失败返回空"设计，失败只影响体验，不影响功能 |
| 4 | 用户误以为"程序知道我的快捷键" | 提示文案必须写明"请手工填写微信设置里的值"，**不能只写"识别失败"**，否则用户会一直以为自己用错了 |

---

### 14.4 需求 2：账号切换与唤起微信（T19）

#### 14.4.1 现状事实

| 事实 | 证据 |
|---|---|
| 账号列表来源：上游 `GET /api/chat/accounts`（**实测结构见 §14.4.1.1**）；拿不到时**扫盘兜底** `data/wechat/output/databases/*` 的目录名 | [WechatAccountEnumerator.cs L49-L129](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Bootstrap/WechatAccountEnumerator.cs#L49-L129)（等价 [launcher.py L327-L370](file:///d:/code/chatlog-ai-portable/app/launcher.py#L327-L370)） |
| "**是否已解析**"的判据（**现有实现**）：`data/wechat/output/account_keys.json` 中该账号条目有非空 `db_key` | [launcher.py L313-L324](file:///d:/code/chatlog-ai-portable/app/launcher.py#L313-L324) |
| **权威判据（本次实测得到，T19 应改用它）**：`accountInfos[]` 每项的 `keyReady` / `switchable` / `hasDecryptedDbs` | 见 §14.4.1.1 |
| "解析新账号"的动作：打开 `http://127.0.0.1:10392` 授权页，用户选账号 → 完成后该文件出现新条目；过程靠**轮询该文件**判定 | [launcher.py L419-L433](file:///d:/code/chatlog-ai-portable/app/launcher.py#L419-L433) |
| 生效账号 = `config/config.json` 的 `wechat_account` | [PROJECT_SCAN §7.1](file:///d:/code/chatlog-ai-portable/docs/PROJECT_SCAN.md#L232-L242) |
| 后端 adapter 按 `(wechat_api, wechat_account)` 缓存，配置一变自动重建 | [server.py L925-L939](file:///d:/code/chatlog-ai-portable/app/server.py#L925-L939) |
| `AutoReplyEngine` **自己也缓存一份** adapter（同样按 key 自动重建） | [server.py L374-L395](file:///d:/code/chatlog-ai-portable/app/server.py#L374-L395) |
| `automation.ensure_wechat_foreground()` 已具备"唤起 / 前置微信窗口"能力 | [automation.py L234-L296](file:///d:/code/chatlog-ai-portable/app/automation.py#L234-L296) |

#### 14.4.1.1 `GET /api/chat/accounts` 实测响应（2026-09-11，**权威，已取代逆向结论**）

```json
{
  "status": "success",
  "accounts": ["wxid_xxx", "wxid_xxx_224d"],
  "default_account": "wxid_xxx",
  "switchable_accounts": [],
  "switchableAccounts": [],
  "keyReadyAccounts": ["wxid_xxx", "wxid_xxx_224d"],
  "accountInfos": [ { "...单项见下表..." } ],
  "items": [], 
  "switchableAccountInfos": []
}
```

单项（`accountInfos[]` / `items[]` / `switchableAccountInfos[]` 元素）字段：

| 字段 | 含义 |
|---|---|
| `account` / `name` | 账号标识 / 显示名 |
| **`keyReady` / `keysReady`** | **是否已解析（密钥就绪）—— 需求 2 的判据** |
| **`switchable`** | 是否可切换 |
| `hasDecryptedDbs` / `database_count` / `databases[]` | 本地已解密库情况（就绪时 16 个库） |
| `accountDir` / `dbStoragePath` / `wxidDir` / `path` | 本地数据库目录 / 微信原始库目录 / 账号目录 |
| `dbKeyPresent` / `imageKeyPresent` / `imageXorKeyPresent` / `imageAesKeyPresent` | 各类密钥是否齐 |
| `keysUpdatedAt` | 密钥更新时间 |
| `realtime` | `{ available, connected, dllPresent, keyPresent, sessionDbPath, error, recentFailure, failureReason, retryAfterSeconds }` |
| `dataSourceStatus` | `{ preferredSource, activeSource, fallbackActive, reason, message, retryAfterSeconds }` |

> **★ 现有实现的一个真实缺陷（T19 必须一并修）**：上游的 `accounts` 是**字符串数组**，而 [WechatAccountEnumerator.cs L63-L89](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Bootstrap/WechatAccountEnumerator.cs#L63-L89) 与 [launcher.py L335-L341](file:///d:/code/chatlog-ai-portable/app/launcher.py#L335-L341) 都按"**对象**数组"逐项取 `account` / `name`，碰到字符串就 `continue` 跳过 → **"接口优先"这段逻辑在当前上游版本下永远拿不到数据，一直在走扫盘兜底**（扫盘恰好也能出结果，所以从表现上看不出来）。
>
> 结论：T19 **不要复用这段解析**，改按上面的 `accountInfos[]` 重写；Python 侧（launcher）那处顺手一起修正，否则"启动选账号"与"运行期切账号"会用两套不一致的判据。

#### 14.4.2 三条分支（按你的补充）

头像菜单点开后，对每个账号给出**状态**与**动作**：

| 分支 | 触发条件 | 行为 |
|---|---|---|
| ① 解析新账号 | `accountInfos[]` 中该项 `keyReady == false`（或 `switchable == false`） | 菜单项旁给「解析此账号」：打开 10392 授权页 + 客户端显示"等待授权"遮罩 + 轮询 `keyReady`（兜底再看 `account_keys.json`），完成后该项变为"可切换"。**未解析前不允许切换**——切过去也是一片空数据 |
| ② 切换生效 | 目标账号已解析 | 写 `wechat_account` → 重建/重置受影响的运行时状态（见 §14.4.4）→ **弹一次提醒**（见 §14.4.3） |
| ③ 唤起微信窗口 | 任意时刻 | 调 `ensure_wechat_foreground()`，不改账号、不写配置 |

#### 14.4.3 ⚠️ 最高风险：账号不一致 = 发错人

这是整批需求里唯一一个**会造成真实外部后果**的缺陷，必须写死对策：

- **数据源读的是账号 B 的数据库，但键盘自动化作用在"微信客户端当前登录的窗口"上。** 如果客户端仍登录 A，自动回复 / 定时发送会把"基于 B 的聊天记录生成的回复"发到 **A 账号**的会话里。
- **程序无法可靠自证"客户端当前登录的是哪个账号"**：上游给的是"数据源解析到的账号"，不等于微信客户端的登录态。本次专门验证了上游两个疑似可用的接口，结论都是"不行"：
  - `GET /api/current-account`（"检测当前登录账号"）：**它探测的是企业微信（WXWork）的登录目录**——实测返回 `current_account: null`，message 里列的是 `D:\QIYEWeChatFile\all_users\login` 这类路径。**对个人微信无效，这条路走不通。**
  - `GET /api/wechat/status`：只能告诉"微信在不在跑"（`is_running / pid / exe_path`），**告诉不了登录的是谁**。
- 因此对策是双保险，缺一不可：
  1. **切换后必须弹一次强提示**（用户原话）："数据源已切换到 B，请把微信客户端的登录账号切回 B，否则自动回复 / 定时发送会把消息发到当前登录账号的会话里。"
  2. **切换后自动暂停两个引擎**（`auto-reply.enabled=false` 与 `scheduled-send.enabled=false`，或至少暂停发送），由用户显式确认"微信已切回 B"后再恢复。宁可多一次点击，也不要一条消息发错人。

#### 14.4.4 切换时必须做的清理（逐条，漏一条就有脏数据）

| # | 项 | 为什么 |
|---|---|---|
| 1 | 前端清空 `Chats` / `Messages` / `SelectedChat` / `AiThread` / `SearchText` 与选中项 | 否则会出现"列表里是 A 的会话、消息是 B 的"，选中项还指向已不存在的 id |
| 2 | 重新读取自动回复配置 | 配置**按账号分文件**：`config/auto-reply/{account}.json`（[PROJECT_SCAN §7.4](file:///d:/code/chatlog-ai-portable/docs/PROJECT_SCAN.md#L266-L271)） |
| 3 | 重新读取定时发送配置 | 同上，`config/scheduled-send/{account}.json`（[§7.4.1](file:///d:/code/chatlog-ai-portable/docs/PROJECT_SCAN.md#L273-L286)） |
| 4 | **重置 `AutoReplyEngine` 的基线指纹** | 引擎"首轮只做基线"的机制在切换账号后**不会自动重置**（[server.py L374-L395](file:///d:/code/chatlog-ai-portable/app/server.py#L374-L395) 只重建 adapter，不重置指纹）。旧指纹对不上 B 的会话 → 会被判成"有新消息" → **刚切完账号就自动回复一条** |
| 5 | 重新拉取定时发送运行状态 | `data/scheduled-send-state.json` 里的任务 id 来自各自账号的配置，切换后要按新账号的任务重新匹配 |
| 6 | 刷新余额 / 模型状态？ | **不需要**——余额与供应商与微信账号无关，别做无意义刷新 |
| 7 | 切换动作要**串行化** | `server.py` 里只有 `_AUTOMATION_LOCK` 一把锁，**配置写入没有锁**；切换写的 `config.json` 与"保存定时任务"写的 `config.json` 并发时，后写的一方会覆盖前写的一方 |

#### 14.4.5 接口与 UI 草案

```
GET  /api/wechat/accounts                → { accounts: [{ account, name, keyReady, switchable, active, hasDecryptedDbs }] }
POST /api/wechat/account/resolve         → { account } → 打开授权页，返回"已开始等待"
GET  /api/wechat/account/resolve-status  → 轮询用；返回 { account, keyReady }
POST /api/wechat/account/switch          → { account } → 切生效账号 + 暂停两个引擎 + 返回提醒文案
POST /api/wechat/activate-window         → 唤起微信窗口（等价 automation.ensure_wechat_foreground）
```

> `keyReady` **优先取上游 `accountInfos[].keyReady`**（权威、实时），`account_keys.json` 只作兜底。两处判据若不一致，会出现"启动时能选、运行时说没解析"的分歧。

#### 14.4.6 风险

| # | 风险 | 说明 |
|---|---|---|
| 1 | 发错人（§14.4.3） | 最高等级，靠"强提示 + 自动暂停引擎"双保险 |
| 2 | 切换瞬间后台线程正在发送 | 与 §14.4.4 第 7 条同源，切换应先停两个引擎的发送、再改配置、再重启引擎 |
| 3 | 上游"扫盘兜底"出来的账号可能是**过期目录**（上次导出的残留） | 切换前用 `keyReady` 筛一遍；**未就绪的只提供"解析此账号"，不展示为可切换** |
| 4 | 多账号下"朋友圈 / 联系人"也各自独立 | 切完账号后这两个模块的缓存必须一起清（T22 / T23 落地时注意） |
| 5 | 同一台机器会出现 `wxid_x` 与 `wxid_x_224d` 两个"账号"（**实测当前就是这种**） | **去重规则必须保留**（[WechatAccountEnumerator L131-L154](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Bootstrap/WechatAccountEnumerator.cs#L131-L154)）："是别人前缀 + 下划线"的项丢弃，否则用户要在两个"同一个人"之间做选择 |

---

### 14.5 需求 3：启动免责声明（T17）

#### 14.5.1 文案（原文有一处必须改的错字）

用户原文：

> 本程序仅供个人学习用途，严谨用于商业用途，违则后果自行承担。该程序不会泄漏用户任何数据。使用该程序造成的一切后果由使用者个人承担。特此说明

**「严谨」应为「严禁」**——这是免责声明的核心效力所在，错字会让整句失去意义，必须改。这句话我按下面处理（其余两处只提建议、不擅自改）：

- 必改：`严谨用于商业用途` → `严禁用于商业用途`
- 建议（待你确认）：
  - `违则后果自行承担` → `违者后果自行承担`（"违者"指人，"违则"读起来像"违反某个规则"）
  - `该程序不会泄漏用户任何数据` → 建议补一句边界，例如"该程序仅在本地读取数据，不会将任何数据上传至第三方"，否则"不会泄漏"是一个无法自证的绝对承诺

#### 14.5.2 形态与时机

- **用自定义 `Window`，不要用 `MessageBox`**：文案有四段，`MessageBox` 不适合承载；且将来若要加"我已阅读并同意"勾选，`MessageBox` 做不到。
- **时机：主窗口首帧渲染之后再弹**，不要放在 `StartupWindow` 之前。理由：它是纯告知，不该阻塞"启动 → 起服务"这条链路；反过来放在最前面会让用户以为程序卡在同意页。
- **每次启动都弹**（需求原话"程序启动页面弹出提示框"），并提供**「不再提示」勾选项**（已确认要做）。勾选状态写入客户端本地设置（T18 建的那个存储），下次启动直接跳过。
  - 配套要求：设置界面里要有**「重新显示免责声明」**入口，否则用户勾错了没有回头路。

#### 14.5.3 明确不做

- 不做"不同意就退出"的强制门禁——它是一段声明，不是许可协议；做成门禁只会让用户反感，且没有实际约束力。

---

### 14.6 需求 4：主题切换（T18）

#### 14.6.1 现状

- 配色**唯一来源**是 [Theme.xaml](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Themes/Theme.xaml)，视图里已无字面量色值（§7.6 立的约束）。
- `MarkdownRenderer` 的 7 个写死 Brush 已改成**惰性按 key 取色**（§7.6），能跟着主题走。
- **一个已知的例外**：导出 HTML 是**硬编码色值**（§7.7 已注明"改主题时这两处要一起改"）。

#### 14.6.2 方案

1. 拆成 `Themes/Theme.Light.xaml` 与 `Themes/Theme.Dark.xaml`，**两者定义完全相同的一组 key**（现有 12+ 节全部覆盖）。
2. 运行期切换 = 替换 `App.Resources.MergedDictionaries` 里的那一项（不要 `Clear()` 后重加，那会把其它资源一起清掉）。
3. 深色主题**必须按 WCAG AA 重做**，不是把 §7.6 之前那套深色搬回来——那套的实测对比度只有约 2.6:1，正是被用户否定的原因。
4. 所有控件样式都已自带完整 `ControlTemplate`（§7.6 坑 1），所以换色只需换 Brush，**不需要改模板**。这是 T15 留下的红利，本需求能低成本落地。

#### 14.6.3 三个坑（提前记下来）

| # | 坑 | 说明 |
|---|---|---|
| 1 | **已渲染的 `FlowDocument` 不会自动换色** | Markdown 渲染出的 `Run.Foreground` 是取色那一刻的**快照**。切主题后，已存在的 AI 回答仍是旧色，只有新生成的才变色。**取舍：接受这个限制**（重建所有历史 FlowDocument 代价大且会触发"文档已有父级"风险，见 §7.5 决定 2） |
| 2 | **导出 HTML 的硬编码色值是漏网之鱼** | 它引不到 `DynamicResource`。两个选择：导出时按当前主题生成，或让它固定一套"打印友好"的浅色。**建议后者**（导出的文档是要分享/打印的，不该跟着用户界面变） |
| 3 | **主题是客户端偏好，不要写进后端 `config.json`** | `config.json` 是后端配置（端口 / 账号 / 供应商），主题与它无关。**已确认**：新增客户端自己的本地设置文件（放 §5 的数据目录下，如 `client-settings.json`），T18 顺带把这个存储建起来；免责声明的"不再提示"也存这里 |

---

### 14.7 需求 5：联系人导出（T22 + 后端 T21）

#### 14.7.1 上游接口（2026-09-11 用 `/openapi.json` **校准完成**，路径已修正）

| 方法 | 路径 | 入参 | 说明 |
|---|---|---|---|
| GET | **`/api/chat/contacts`** | `account, source, keyword, include_friends(true), include_groups(true), include_officials(true), include_official_subscriptions, include_official_services, include_former_friends(false), include_blocked(false)` | 通讯录列表。**实际路径带 `/chat` 前缀**，不是逆向时以为的 `/api/contacts` |
| GET | `/api/chat/contacts/profile` | `account, username, source` | 单个联系人资料 |
| POST | `/api/chat/contacts/export` | `ContactExportRequest`：`account, source, output_dir(**必填**), format(html/json/txt/excel，兼容 csv，默认 json), include_avatar_link(true), contact_types, keyword, encrypt, content_key_base64` | 上游自带导出（**直写磁盘**） |
| GET | `/api/chat/contacts/export/style`、POST `/api/chat/contacts/export/seal` | — | 原生导出样式 / 浏览器端导出签名 |

`GET /api/chat/contacts` 实测响应（本机 495 条）：

```
status, account, source, sourceRequested, sourceFallback, sourceFallbackReason,
sourceFallbackMessage, sourceFallbackRetryAfterSeconds, total, counts, contacts
```

- `counts`：`friends / groups / officials / officialSubscriptions / officialServices / officialEnterprises / officialUnknown / formerFriends / blocked / total`（同时给了 snake_case 别名，如 `former_friends`）。
- `contacts[]` 字段（实测）：`username, displayName, remark, nickname, alias, gender, signature, type, country, province, city, region, sourceScene, source, addTime, addTimeText, commonChatroomCount, avatar, avatarLink, pinyinKey, pinyinInitial`
  - `type`：`friend` / `group`（实测群聊为 `"group"`）
  - `avatar`：**上游的头像 URL**，形如 `http://127.0.0.1:10392/api/chat/avatar?account=..&username=..`（username 需 URL 编码）
  - `avatarLink`：微信 CDN 头像地址
- 分类过滤用 `include_*` 布尔开关；`ContactTypeFilter` 结构与之一一对应（`friends / groups / officials / official_subscriptions / official_services / former_friends / blocked`）。

**两条对设计有决定性影响的实测结论**：

1. **该接口没有分页参数**（无 `limit` / `offset`）——一次返回全部。本机 495 条 = 好友 132 + 群 74 + 公众号 24 + 已删除 261 + 黑名单 4。客户端必须自己做虚拟化，**不要指望翻页**；`keyword` 是唯一的收窄手段。
2. **上游的联系人导出不支持"按指定 username 列表导出"**——`ContactExportRequest` 只有 `contact_types`（按分类）与 `keyword`（按关键词）两个筛选维度，**没有 `usernames[]`**。需求 5 要的是"多选"，**所以联系人导出必须由我方自己格式化**（见 §14.7.4）。

#### 14.7.2 后端代理设计

```
GET /api/contacts?include_friends=true&include_groups=true&include_former_friends=false&keyword=xxx
→ { "status": "success", "total": 495, "counts": { friends, groups, officials, ... }, "contacts": [ {...} ] }
```

- **入参直接对齐上游的 `include_*` 布尔开关**，不要自创 `scope=friend,group` 这种语法：多一层映射就多一处漂移，而上游那 8 个开关已经够用。
- 走本项目自己的接口而不是让客户端直连 10392：客户端只认 `18765` 一个地址（[API_REFERENCE §0](file:///d:/code/chatlog-ai-portable/docs/API_REFERENCE.md#L9-L19)），且上游端口可配，直连会把端口写死在客户端。
- **不要把上游返回原样透传**：字段要做一次归一（与 `adapters.py` 对消息的做法一致），否则上游改字段会直接打穿到 UI。
- **`avatar` 不能在响应里原样透传**：上游给的是 `http://127.0.0.1:10392/api/chat/avatar?...`，直接给客户端等于把上游端口暴露进 UI。应由本服务提供一个 `/api/contacts/avatar?username=..` 转一次（与 §14.8.2 的媒体代理同一模式）。

#### 14.7.3 客户端 UI

- 列表：`ListBox` + 每行 `CheckBox`，顶部一行「全选 / 反选 / 已选 N 项」+ 「导出」按钮。
- **分类筛选**（好友 / 群聊 / 公众号 / 已删除 / 黑名单）是必须的：全量通讯录可能几百上千条，没有分类只能靠搜索。
- 搜索框：对应上游 `keyword` 参数（服务端过滤）。
- **列表必须虚拟化**：接口没有分页（§14.7.1），本机一次就是 495 条，重度用户可能上千。用 `ListBox`（自带虚拟化），**不要用 `ScrollViewer + ItemsControl`**——后者是本项目在别处（守护对象、定时任务）刻意选的，但那些列表只有个位数行，规模不同、选择也应不同。
- **头像默认不加载**：495 条每次滚动都请求头像，对本地服务也是无谓压力。默认显示首字母圆形头像（与现有会话列表的 `AvatarText` 一致），或仅在详情区加载。
- 空选中时点导出 → 明确提示"请先选择要导出的联系人"，**不要**静默导出全部（那是"全选"按钮的语义，混淆两者会让用户导出一份自己没预期的完整通讯录）。

#### 14.7.4 导出：**自己格式化**（不走上游导出）

**已确认走我方自己格式化**——这是实测出来的硬约束，不是偏好：

| 原因 | 说明 |
|---|---|
| 上游导出**不支持多选** | §14.7.1 结论 2：`ContactExportRequest` 没有 `usernames[]`。需求 5 要的正是"选几个导几个"，上游做不到 |
| 上游导出要求 `output_dir` **必填**且**直写磁盘** | 客户端要先替用户决定一个目录（或调 `/api/system/pick_directory`），导出完还得告诉用户"文件在哪"，比"另存为"多绕两道 |
| 数据已经全在手上 | `/api/chat/contacts` 一次返回全部联系人（§14.7.1），不需要上游帮忙落盘 |

实现要点：

- 格式复用现有 `ExportFormat`（Markdown / HTML / JSON / CSV），落盘仍走系统"另存为"（[ExportModels.cs](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Models/ExportModels.cs)）。
- **新建独立的 `ContactExporter`，不要去改 `ChatExporter`**。理由与 §7.7 决定 4 完全相同：`ChatExporter` 的输入是"某个会话的消息"，联系人导出是"一批联系人的属性"，输入类型都不一样，硬凑一个类只会把两件不相干的功能焊死。
- CSV 仍带 BOM（Excel 中文乱码的解药），列建议：`用户名 / 显示名称 / 备注 / 昵称 / 微信号 / 性别 / 地区 / 来源 / 添加时间`。
- **导出的内容里不要带 `avatar` 那个指向 `127.0.0.1:10392` 的 URL**（本机临时地址，发给别人就是死链）；要么不带头像，要么只用 `avatarLink`。

#### 14.7.5 风险

| # | 风险 | 说明 |
|---|---|---|
| 1 | ~~接口路径 / 参数未确认~~ | **已用 `/openapi.json` 校准**（§14.7.1），路径修正为 `/api/chat/contacts` |
| 2 | 头像加载量大 | 列表默认不加载头像；头像走本服务代理，避免把上游端口暴露给 UI |
| 3 | 分类口径与用户预期不一致 | "公众号"在微信里分订阅号 / 服务号 / 企业号三类；`counts` 里三个键都有，UI 要么分开写清楚，要么合并成一项并在文案里说明范围 |
| 4 | 全量返回的响应体可能很大 | 尚未验证"上千联系人 + `include_avatar_link`"时的响应大小与耗时；若明显变慢，退路是**在本服务侧**只取必要字段并分页（**不要**改上游） |

---

### 14.8 需求 6 / 7：朋友圈查看与导出（T23 + 后端 T21）

#### 14.8.1 上游接口（2026-09-11 用 `/openapi.json` **校准完成**）

| 方法 | 路径 | 关键入参（默认值） | 说明 |
|---|---|---|---|
| GET | `/api/sns/self_info` | `account, source(auto)` | 自己的 `{wxid, nickname, source}` |
| GET | `/api/sns/timeline` | `account, limit(20), offset(0), usernames, keyword, source(auto)` | 时间线。**`usernames` 是字符串（逗号分隔），不是数组**；另有 `keyword` 可做内容搜索 |
| GET | `/api/sns/users` | `account, keyword, limit(5000)` | 发过朋友圈的人 + `postCount` |
| POST | `/api/sns/realtime/sync_latest` | `account, max_scan(200), force(0)` | **实时朋友圈增量同步到解密库**——看新版动态前应先调它。**无新数据时返回 `status:"noop"`，属成功语义**（见下方实测陷阱） |
| GET | `/api/sns/media` | `account, create_time, width, height, total_size, idx, post_id, media_id, post_type(1), media_type(2), md5, token, key, use_cache(1), url, variant` | 图片（`variant=full` 取原图）；参数多，但每一项都是"定位那一张图"所必需 |
| GET | `/api/sns/video` | `account, post_id, media_id` | 本地缓存视频 |
| GET | `/api/sns/video_remote` | `account, url, token, key, use_cache(1)` | 远程视频 / 实况回落 |
| GET | `/api/sns/article_thumb` | `url`（**必填**） | 公众号文章封面 |
| GET / POST | `/api/sns/exports` | POST 体见 `SnsExportCreateRequest` | 导出任务（列出 / 创建） |
| GET / DELETE | `/api/sns/exports/{export_id}` | — | 查状态 / 取消 |
| GET | `/api/sns/exports/{export_id}/events`、`/download` | — | SSE 进度 / 下载 ZIP |

**`sync_latest` 的一个实测陷阱（2026-09-11 踩到）**：**无新增数据时上游返回 HTTP 200 + `status: "noop"`**。实测 payload：
`{"status":"noop","scanned":200,"upserted":0,"maxId":"...","lastMaxId":"...","elapsedMs":1307}`，语义是**幂等成功**（扫了 200 条、新增 0 条、游标未动）。

- 本项目适配层的成功判据是 `status ∈ {success, completed, 0, None}`（`adapters.py` 的 `_get` / `_post`），`noop` 会落进 else 抛错 → 端点转成 `503 {"detail": "<整段 payload>"}` → 用户在界面上看到"同步失败"外加一串 Python dict 串。
- **约束：不要放宽全局白名单**。那会让 contacts / timeline / export 等端点上的任何 `noop` 都算成功，风险面过大。正确做法是**按端点放行**：`_post(..., ok_statuses=("noop",))`，且只有 `sns_sync_latest` 传这个参数。
- 将来若再遇到"成功语义的非 success 状态"，**沿用同一方式按端点放行**，不要动全局判据。
- 顺带：适配层抛错时把**整段上游 payload** 写进错误消息，会一路进到 UI 与 `logs/chatlog.log`。这条消息应当只带 `status` 或截断后的 payload，不要全量 dump（§10 R10 把 `logs/` 列为禁止外发）。

实测响应结构（`GET /api/sns/timeline?limit=2`）：

```
timeline, hasMore, limit, offset, source, cover, covers,
sourceRequested, sourceFallback, sourceFallbackReason,
sourceFallbackMessage, sourceFallbackRetryAfterSeconds
```

- `timeline[]` 字段（实测）：`id, tid, username, displayName, createTime, contentDesc, location, sourceName, media, likes, comments, type, title, contentUrl, finderFeed, official`
- `covers[]` 字段：`id, tid, username, createTime, media, type`（封面图）
- `GET /api/sns/users` 实测：`{ items: [{ username, displayName, postCount }], count, limit }`
- `SnsExportCreateRequest`（权威）：`account, scope(selected|all，默认 selected), usernames[], format(html|json|txt|excel，默认 html), use_cache(true), output_dir(可空，不填走默认目录), file_name, encrypt(false), content_key_base64`

#### 14.8.1.1 `media[]` 实测字段（2026-09-11，**决定了 T21 的媒体接口设计**）

```
type, id, url, thumb, urlAttrs, thumbAttrs, size

urlAttrs   = { type, md5, key, enc_idx, token }
thumbAttrs = { type, key, enc_idx, token }     ← 缩略图**没有 md5**
size       = { width, height, totalSize }
```

三条实测事实，每一条都影响设计：

1. **同一个 media 有两个不同的 token**：实测 `urlAttrs.token` ≠ `thumbAttrs.token`（结尾明显不同），而 `key` 相同。也就是说"取原图"和"取缩略图"必须各用自己那一套 attrs，**不能"一套参数 + 一个 variant 开关"**。
2. **缩略图没有 md5**，而 `/api/sns/media` 的 `md5` 是可选的 —— 所以 `variant=thumb` 时不要传 md5。
3. **存在"根本没有媒体定位参数"的 media**：实测 `item.type=28`（视频号）的 `media[0]` 是 `type=0`、`url` 有 931 字符，而 `thumb / urlAttrs / thumbAttrs / size` **全为空**。这类走不了 `/api/sns/media`，客户端必须退化为外链文本或占位，**九宫格要能容忍空格子**。

> 一个实测现象值得记下：本机读到的 `source` 是 `sqlite` / `decrypted` 而非 `realtime`，并带 `sourceFallback=true` + `sourceFallbackReason: "WCDB connection recently failed; retry after 30s"`。这是"实时库打不开 → 回退到已解密库快照"的**正常降级**，不是接口坏了；但 UI 必须把它透出来，否则用户会以为朋友圈停更了。

#### 14.8.2 后端代理设计（关键：token / key 不下发前端）

```
GET  /api/sns/timeline?limit=20&offset=0&usernames=a,b
→ { status, timeline: [...], has_more, next_offset, source_fallback }

POST /api/sns/media                                  ← 定位参数走 POST body，后端无状态转发
GET  /api/sns/video?post_id=..&media_id=..           ← 上游这条不需要 token/key，保持 GET
POST /api/sns/sync-latest                            ← 代理上游 /api/sns/realtime/sync_latest
```

- **媒体接口必须由后端代理**（不把上游 10392 暴露给客户端），但 **参数由客户端回传，后端不缓存**。理由就是 §14.8.1.1 那三条实测事实：同一 media 的原图 / 缩略图是**两个不同的 token**、缩略图**没有 md5**、还有一类 media **根本没有参数**。若让后端"凭 post_id/media_id 自己还原参数"，就必须按 `(post_id, media_id, variant)` 建缓存，还要为"永远没有参数"的那类长期返回错误 —— 那是自己造一个"图片偶尔打不开"的缺陷。
- **参数放 POST body，不放 query string**：uvicorn 的 access log 会把 query 一并写进 `logs/chatlog.log`，而 §10 R10 已把 `logs/` 列为含密钥、禁止外发。走 body 则既不进日志也不进 URL。客户端拿到 `urlAttrs` / `thumbAttrs` 后**按"要原图还是要缩略图"整套回传**，不要自己拆字段、也不要让后端按 variant 去挑。
- 后端职责因此退化为：**字段白名单 + 长度上限 + 转发 + 回传字节流**，不做缓存、不落盘。
- timeline 的 `usernames` 是**逗号分隔字符串**（不是数组），它直接支撑"只看某人的朋友圈"；后端要做参数归一——不传 = 全部，**不要传空串或 `[""]`**。
- **`sourceFallback` 必须透出到 UI**：它是"实时库连不上、当前看的是解密库快照"的信号，用户据此才知道数据可能不是最新的（详见 §14.8.1 末段）。

#### 14.8.3 客户端 UI

- 列表栏：动态按日期分组；顶部可按人筛选（数据来自 `/api/sns/users`）+ 一个「同步最新」按钮（调 `/api/sns/sync-latest`）。
- **进模块先同步一次**：`sync_latest` 是把实时朋友圈增量拉进解密库的动作，不调它可能看到旧数据；进模块调一次即可，**不要每次滚动都调**。
- 详情区：头像 + 昵称 + 时间 + 文字 + **图片九宫格**（点击可看大图）+ 点赞 / 评论列表。
- **滚动到底自动加载下一页**（20 条/页），不要一次拉全量。
- 图片**懒加载 + 缩略图优先**（先 `variant=thumb`，点开才取 `full`）。
- **本模块无 AI 分析区**（你已确认"朋友圈不做总结"），详情区右栏留给"导出"等动作，或直接全宽展示。

#### 14.8.4 导出：**走上游导出**（已确认）

| 路线 | 决定 | 理由 |
|---|---|---|
| **A. 代理上游导出** | **采用** | 朋友圈的图片 / 视频都在微信本地缓存里，自己格式化要处理"图片怎么进导出文件"；上游已经把这部分做完了 |
| B. 自己格式化 | 不采用 | 同上，成本远超收益 |

A 路线的实现要点：

- **上游支持 `scope=selected` + `usernames[]`**（与联系人导出不同，见 §14.7.4）——"只导某人 / 多选几个人"这条路是通的。
- `output_dir` 可空（上游会落到默认目录），但导出产物是 ZIP。**建议统一走"上游生成 → 我方 `/download` 代理回传给客户端 → 客户端另存为"**，不要把上游的输出目录路径暴露给用户。
- 进度优先用 `GET /api/sns/exports/{id}` **轮询**，第一版不要上 SSE（`/events`）：WPF 侧要自己处理流式读取与断线重连，不值得。
- UI 上要注明"**导出格式由微信数据源提供**（html / json / txt / excel + ZIP 打包）"，避免用户以为能选 Markdown。

#### 14.8.5 风险

| # | 风险 | 说明 |
|---|---|---|
| 1 | ~~接口字段全靠逆向~~ | **已用 `/openapi.json` 校准并实测响应**（§14.8.1） |
| 2 | 图片 / 视频量大 | 懒加载 + 缩略图优先 + `use_cache`；不要把原图直接塞进内存 |
| 3 | 上游解析器有效期 | 属 §10 R9 的既有风险，朋友圈数据依赖同一套解密链路 |
| 4 | "不做总结"的边界要说清 | 朋友圈模块**不出现**「生成分析」按钮，也不走后端 `/api/summarize`；避免以后有人顺手接上去 |
| 5 | `sourceFallback=true` 时用户以为"没数据 / 停更" | UI 必须显式提示"实时库暂不可用，当前为解密库快照"，并给重试入口 |
| 6 | 导出是**异步任务 + 落盘**，不是一次请求 | 要处理：任务还在跑时用户关窗、任务失败、取消（`DELETE /api/sns/exports/{id}`）。第一版至少要有「取消」与明确的失败文案 |

---

### 14.9 新增后端接口草案（汇总，T21 及配套）

| # | 方法 | 路径 | 代理到（上游） | 归属需求 |
|---|---|---|---|---|
| 1 | GET | `/api/contacts` | `/api/chat/contacts` | 5（T22） |
| 2 | GET | `/api/contacts/avatar` | `/api/chat/avatar` | 5（T22） |
| 3 | GET | `/api/sns/timeline` | `/api/sns/timeline` | 6（T23） |
| 4 | GET | `/api/sns/users` | `/api/sns/users` | 6（T23） |
| 5 | POST | `/api/sns/sync-latest` | `/api/sns/realtime/sync_latest` | 6（T23） |
| 6 | GET | `/api/sns/media` | `/api/sns/media` | 6（T23） |
| 7 | GET | `/api/sns/video` | `/api/sns/video` | 6（T23） |
| 8 | POST | `/api/sns/export` + GET `/api/sns/export/{id}` + GET `/{id}/download` | `/api/sns/exports` 系列 | 7（导出，路线 A） |
| 9 | GET | `/api/wechat/accounts` | `/api/chat/accounts`（取 `accountInfos[]`） | 2（T19） |
| 10 | POST | `/api/wechat/account/resolve` + GET `resolve-status` | 打开 10392 授权页 + 轮询 `keyReady` | 2（T19） |
| 11 | POST | `/api/wechat/account/switch` | 写 `config.json` + 重启两个引擎 | 2（T19） |
| 12 | POST | `/api/wechat/activate-window` | 无（本地 `automation.ensure_wechat_foreground`） | 2（T19） |
| 13 | POST | `/api/auto-reply/detect-hotkeys` | 无（本地探测骨架，当前恒返回空） | 1（T20） |

> 全部按本项目既有约定：返回 `{"status": "success", ...}`、错误走 `{"detail": "..."}`、端口仍是 `18765`、`/api/*` 前缀（[API_REFERENCE §0](file:///d:/code/chatlog-ai-portable/docs/API_REFERENCE.md#L9-L43)）。落地后要**回填进 `docs/API_REFERENCE.md`**（本文件只做规划）。

---

### 14.10 取证与决策记账（2026-09-11 **已全部闭环**）

**已完成的两项取证**（详细结论见 §14.3.0 / §14.7.1 / §14.8.1）：

| # | 取证 | 结论 |
|---|---|---|
| 1 | 启动上游 `wechat-backend.exe`，读 `http://127.0.0.1:10392/openapi.json`（只读；用完**已停止进程**，并确认 10392 端口已释放、无残留进程、无 `.shutdown-requested` 残留） | 全部接口已校准：**联系人真实路径是 `/api/chat/contacts`**（逆向时误记为 `/api/contacts`）；朋友圈接口与逆向一致，并补出了此前不知道的 `sync_latest`、`keyword`、`covers` 等 |
| 2 | 微信 4.1 快捷键可读性 | **读不到**（注册表是空键；账号级配置是加密二进制）→ T20 降级 |

**已确认的六项决策**：

| # | 决策 | 结论 |
|---|---|---|
| 1 | 微信账号切换 | 「切换账号」与「唤起窗口」**都要**；未解析的账号先走「解析此账号」；切换后必须提示"把微信登录切回该账号"（§14.4） |
| 2 | 朋友圈是否做 AI 总结 | **不做**，只查看 / 导出（§14.8.5 风险 4） |
| 3 | 联系人数据源 | **上游通讯录接口**；导出用我方自研格式（上游导出不支持多选，§14.7.4） |
| 4 | 主题切换形式 | **浅色 / 深色两套**；偏好存客户端本地设置文件（§14.6.3 坑 3） |
| 5 | 免责声明 | 每次启动弹 + **「不再提示」勾选**；设置里有"重新显示"入口（§14.5.2） |
| 6 | 朋友圈导出 | **按上游导出**（异步任务 + ZIP，§14.8.4） |

**仍悬空、但不阻塞开工的两项**：

| # | 事项 | 说明 |
|---|---|---|
| 1 | 免责声明另外两处措辞（`违则` → `违者`；"不会泄漏任何数据"补一句边界） | §14.5.1 的建议，尚未明确答复；**`严谨 → 严禁` 属错字，已按必改处理** |
| 2 | 窗口最小尺寸 `MinWidth` | T16 完成后按新布局重算（会比现在的 1490 小） |

---

### 14.11 顺带发现（本次读 `/openapi.json` 看到，**不在本批范围**）

记在这里是因为它们影响后续决策，**本次不规划、不实施**：

| 上游接口 | 说明 | 为什么值得记 |
|---|---|---|
| `GET /api/chat/exports` + `/targets` + `/{id}` + `/{id}/download` + `/{id}/events` | 上游**自带一套完整的聊天记录导出**（异步任务 + ZIP + 进度） | T11 已自研 `ChatExporter`。**两套并存不算错**（自研的在格式与交互上更贴本产品），但将来若有人问"为什么不用上游那套"，答案在这里 |
| `GET /api/general/friend-verifications` | 好友验证记录 | 以后要做"新的朋友"面板时不必自己解析数据库 |
| `GET /api/chat/search` + `POST /api/chat/search-index/build` | 消息全文搜索（需先建索引） | 目前只能靠 `keyword` 过滤会话，做不了消息内容搜索 |
| `GET /api/system/pick_directory` | 让上游弹目录选择框 | 用上游导出时可能用到（§14.8.4 选择不暴露目录，故第一版不用） |
| `GET /api/wrapped/annual`、`/api/general/*` | 年度总结 / 小程序 / 支付记录 / 撤回记录等 | 上游能力远大于本项目当前用到的 3 个接口 |


