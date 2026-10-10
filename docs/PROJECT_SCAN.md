# Chatlog AI 便携统一版 — 项目扫描档案

> ⚠️ **部分内容已作废（2026-09-30，阶段 D3）**：本文档第 3 / 4 节里关于
> **上游微信解析服务（`runtime/wechat`、`wechat-backend.exe`、10392 端口、broker、授权页、
> `.bat` 启动入口）**的描述**不再反映现状**。自研读取链路（`docs/SELFHOST_DATA_PLAN.md` §4 阶段 C）
> 已接管会话/消息、通讯录+头像、朋友圈时间线+用户、朋友圈导出；`runtime/wechat` 已被移除，
> 启动编排改由 .NET 客户端（`StartupOrchestrator`）承担，且上游已是**可选**的。
> 该改造的完整口径与验收证据，以 `docs/SELFHOST_DATA_PLAN.md` 为准。

> 用途：本文件是项目的「静态快照」。新开对话窗口（上下文已满载）时，**先读本文件即可免去重新扫描**。
> 本文件只描述现状与机制，不含改造方案。接口细节见 `docs/API_REFERENCE.md`。
>
> 扫描时间：2026-09-10　项目版本：`app/server.py` 中 `FastAPI(version="5.0.0")`
> 根目录：`d:\code\chatlog-ai-portable`

---

## 1. 一句话定位

在本机启动「微信数据源 + AI 聚合服务」两个本地进程，用一个网页展示真实会话与聊天记录，并把当前筛选范围内的文本交给 OpenAI 兼容的大模型做总结 / 待办提取 / 自动回复。发行包内置 Python 便携运行时，终端用户无需安装开发环境。

---

## 2. 目录结构（只列关键项，忽略 `runtime/` 内第三方运行时）

```
chatlog-ai-portable/
├─ 启动Chatlog-AI.bat         # 普通启动入口（调 launcher.py）
├─ 首次初始化.bat             # 强制重新授权入口（同上，加 --initialize）
├─ README.md                  # 面向终端用户的说明
├─ 教程/首次使用说明.txt       # 面向终端用户的分步操作说明
├─ .gitignore                 # 忽略规则（必须 UTF-8；详见 §7.7）
├─ config/                    # 唯一配置根目录（★ 除 config.example.json 外全部不入库）
│  ├─ config.json             # 主配置：端口、下游地址、选定账号、生效供应商（★不入库，启动时自动生成）
│  ├─ config.example.json     # 配置示例（键名齐全，值为脱敏假数据）（入库）
│  ├─ api-keys.json           # 模型供应商与 API Key（providers[]）（★不入库，启动时自动生成）
│  ├─ prompts/                # 全局静态提示词（纯文本 .txt，可直接编辑）（★不入库，启动时自动生成）
│  ├─ auto-reply/{account}.json  # 自动回复配置，按微信账号隔离（★不入库）
│  └─ scheduled-send/{account}.json  # 定时发送配置，按微信账号隔离（★不入库）
├─ app/                       # 本项目的全部业务代码
│  ├─ launcher.py             # 进程编排 + 两阶段启动状态机（560 行）
│  ├─ server.py               # FastAPI 后端（约 1650 行）
│  ├─ config_defaults.py      # 默认配置 / 默认提示词的单一事实源（30 行）
│  ├─ adapters.py             # 微信数据源适配器（165 行）
│  ├─ automation.py           # Windows 键盘/剪贴板自动化（331 行）
│  └─ static/                 # 前端（已拆分为三文件，见 §6）
│     ├─ index.html           # 215 行，仅 DOM 结构
│     ├─ style.css            # 227 行，全部样式
│     └─ app.js               # 891 行，全部逻辑（59 个顶层函数）
├─ data/                      # 运行时数据（禁止外发）
│  ├─ summaries.json          # 会话总结历史缓存（首次分析后生成）
│  ├─ scheduled-send-state.json # 定时发送运行时状态（last_sent_at / pending_fire_at）（首次发送后生成）
│  ├─ .shutdown-requested     # 退出标记文件（运行时生成）
│  └─ wechat/{data,output}/   # 微信数据源输入/输出目录（account_keys.json 等由上游自写）
├─ logs/                      # chatlog.log / wechat.log
├─ runtime/                   # 内置 Python 便携运行时 + 微信后端
│  ├─ python/                 # 便携 Python 解释器（已裁剪标准库）
│  ├─ python-packages/        # 纯 Python 站点包（fastapi / uvicorn / pydantic 等）
│  └─ wechat/resources/       # 微信后端与其原生库、内置 ffmpeg
├─ licenses/                  # 上游组件许可证（Python / WeChatDataAnalysis / WeFlow）
└─ docs/                      # 本文档与 API_REFERENCE.md
```

**配置归属原则**：所有本项目自有的配置类文件一律放在 `config/` 下；`data/wechat/output/` 内的 JSON 由微信上游工具自写，属于外部数据源，保持原位不动。


---

## 3. 双服务架构与端口

| 服务 | 默认地址 | 进程来源 | 作用 |
|---|---|---|---|
| Chatlog AI（本服务） | `http://127.0.0.1:18765` | `app/server.py`（uvicorn） | 对前端提供 REST API，聚合下游数据源，调用大模型 |
| 微信数据源 | `http://127.0.0.1:10392` | `runtime/wechat/resources/backend/wechat-backend.exe` | 读取微信本地数据库，提供会话/消息 HTTP 接口与授权页 |

端口常量定义在 [launcher.py](file:///d:/code/chatlog-ai-portable/app/launcher.py#L45-L46)：`CHATLOG_PORT=18765`、`WECHAT_PORT=10392`。

**关键点：两个服务都是 `127.0.0.1` 本地绑定，不对外网开放。** 本服务通过 `config/config.json` 里的 `wechat_api` 找到下游，因此下游端口可改（改 `config/config.json` 即可，无需改代码）。

另有 `wechatdb_broker.exe`（原生库 broker）由 launcher 以 `--parent-pid` 方式随微信数据源一起拉起，它不是 HTTP 服务，不占端口，详见 §4。

---

## 4. 启动流程（`launcher.py`）

**核心设计：两阶段阻塞式状态机**——普通启动会先判定授权状态，未授权时原地进入授权阶段，授权完成后自动继续，用户无需关闭窗口再重新双击。

1. `.bat` 双击 → 运行 `app/launcher.py`。
2. `run()` 计算 `authorize_needed = force_initialize or not has_wechat_key()`（[L454](file:///d:/code/chatlog-ai-portable/app/launcher.py#L454)）。
   - 未授权且当前不是管理员 → 打印提示后直接退出（[L455-L463](file:///d:/code/chatlog-ai-portable/app/launcher.py#L455-L463)）。**这是刻意的：读微信数据库必须提权，与其中途失败不如尽早失败。**
3. `ensure_directories()` 建好 `logs/`、`data/wechat/{data,output}` 等运行时目录（[L169-L175](file:///d:/code/chatlog-ai-portable/app/launcher.py#L169-L175)）。
4. **`ensure_config_files()` 按 `app/config_defaults.py` 的默认值补齐 `config/`**（[L148-L166](file:///d:/code/chatlog-ai-portable/app/launcher.py#L148-L166)）——`config.json`、`api-keys.json`、`prompts/*.txt` 缺哪个补哪个，**已存在的一律不覆盖**。这一步必须在 `require_paths()` 之前：`config.json` 已不入库，新环境首次启动时若不存在，会被 `require_paths()` 判为"缺少运行文件"而启动失败（详见 §7.7）。
5. `require_paths()` 校验必需运行文件（`wechat-backend.exe`、`resources/ui`、`config.json`）。
6. **启动阶段一：微信数据源。** `ensure_port_free(10392)` → `start_wechat_service()`（[L286-L310](file:///d:/code/chatlog-ai-portable/app/launcher.py#L286-L310)）：
   - 先以 `CREATE_NO_WINDOW` 拉起 `wechatdb_broker.exe`（传入 `--database-root data/wechat/data`、`--parent-pid`），等待 1 秒；
   - 再启动 `wechat-backend.exe`，注入 `build_wechat_env()` 的那组 `WECHAT_TOOL_*` 环境变量；
   - 轮询 `http://127.0.0.1:10392/api/health`，最长 180 秒。
7. **授权分支：**
   - `--initialize` 且已有密钥 → 打开授权页 + `input()` 等待用户回车确认（密钥已存在时无法自动判定完成，[L477-L483](file:///d:/code/chatlog-ai-portable/app/launcher.py#L477-L483)）。
   - 未授权 → 打开 `http://127.0.0.1:10392` 授权页，`wait_wechat_authorization()` 每 0.6 秒轮询 `data/wechat/output/account_keys.json` 是否出现非空 `db_key`（[L427-L433](file:///d:/code/chatlog-ai-portable/app/launcher.py#L427-L433)）。
   - 已授权 → 直接进入阶段二。
8. `prompt_account_selection()`（[L397-L417](file:///d:/code/chatlog-ai-portable/app/launcher.py#L397-L417)）：检测到多个微信账号时在控制台列出候选，把选择写回 `config/config.json` 的 `wechat_account`。
9. **启动阶段二：聚合服务。** `ensure_port_free(18765)` → 启动 `server.py`（uvicorn），注入环境变量：
   - `CHATLOG_ROOT_DIR`：让 `server.py` 定位到项目根目录（打包成 exe 后 `__file__` 不可靠，必须靠环境变量）。
   - `CHATLOG_SHUTDOWN_FILE`：退出标记文件路径，供 `/api/shutdown` 使用。
10. 轮询就绪后自动打开浏览器 `http://127.0.0.1:18765`。
11. 用 `ProcessJob`（Windows Job Object，`KILL_ON_JOB_CLOSE`）把子进程绑定成一组，**父进程退出时整组一起结束**——这是"关闭启动窗口后两个服务一起死"的实现原理。同时主循环每 0.5 秒检查：任一子进程退出 → 抛错整体收尾；`data/.shutdown-requested` 出现 → 收到网页退出请求，整体收尾。

---

## 5. 后端模块职责

### 5.1 `app/server.py`（聚合服务，约 1650 行）

| 区块 | 行号区间 | 职责 |
|---|---|---|
| 路径常量 | [L31-L41](file:///d:/code/chatlog-ai-portable/app/server.py#L31-L41) | `CONFIG_DIR` / `CONFIG_FILE` / `API_KEYS_FILE` / `PROMPTS_DIR` / `AUTO_REPLY_DIR` / `SUMMARIES_FILE` / `STATIC_DIR` |
| 主配置读写 | [L53-L72](file:///d:/code/chatlog-ai-portable/app/server.py#L53-L72) | `load_config` / `save_config`（`config/config.json`），**原子写**（写 `.tmp` 再 `os.replace`）；默认值取自 `config_defaults.DEFAULT_CONFIG` |
| API Key 读写 | [L75-L92](file:///d:/code/chatlog-ai-portable/app/server.py#L75-L92) | `load_api_keys` / `save_api_keys`（`config/api-keys.json`，结构恒为 `{"providers": [...]}`） |
| 提示词读取 | [L94-L103](file:///d:/code/chatlog-ai-portable/app/server.py#L94-L103) | `load_prompt`（`config/prompts/<name>.txt`，缺失或为空时回退 `config_defaults.DEFAULT_PROMPTS`） |
| 供应商预设 | [L107-L144](file:///d:/code/chatlog-ai-portable/app/server.py#L107-L144) | `PROVIDER_PRESETS`：deepseek / kimi / zhipu / qwen / custom |
| 供应商解析 | [L146-L186](file:///d:/code/chatlog-ai-portable/app/server.py#L146-L186) | `list_providers`（读 api-keys）/ `get_active_provider`（active 失效时回落第一个）/ `_mask_api_key` / `_extract_api_error` |
| 大模型调用 | [L187-L213](file:///d:/code/chatlog-ai-portable/app/server.py#L187-L213) | `_chat_completion`：统一走 OpenAI 兼容 `/chat/completions` |
| 余额查询 | [L215-L263](file:///d:/code/chatlog-ai-portable/app/server.py#L215-L263) | `_query_balance`，按 `balance_parser` 解析 deepseek/moonshot |
| 总结历史 | [L265-L272](file:///d:/code/chatlog-ai-portable/app/server.py#L265-L272) | `load_summaries` / `save_summaries`（`data/summaries.json`） |
| 自动回复配置 | [L275-L353](file:///d:/code/chatlog-ai-portable/app/server.py#L275-L353) | `AUTO_REPLY_DEFAULTS` 与按微信账号分文件的读写（`config/auto-reply/{account}.json`） |
| 自动回复引擎 | [L354-L498](file:///d:/code/chatlog-ai-portable/app/server.py#L354-L498) | `AutoReplyEngine` 后台线程 |
| **定时发送（2026-09-11 新增）** | [L513-L922](file:///d:/code/chatlog-ai-portable/app/server.py#L513-L922) | `SCHEDULED_*` 常量、`_AUTOMATION_LOCK`、`load_scheduled_send` / `save_scheduled_send` / `load_scheduled_state` / `save_scheduled_state`、`normalize_scheduled_tasks`、`generate_greeting_text`、`_scheduled_base_due` / `_scheduled_next_display`、`ScheduledSendEngine`；接口见 [L1503-L1610](file:///d:/code/chatlog-ai-portable/app/server.py#L1503-L1610) |
| 适配器单例 | [L925-L927](file:///d:/code/chatlog-ai-portable/app/server.py#L925-L927) | `_WECHAT_ADAPTER` / `_WECHAT_ADAPTER_KEY` 按配置 key 复用实例 |
| 消息时间/过滤 | [L929-L971](file:///d:/code/chatlog-ai-portable/app/server.py#L929-L971) | `message_epoch`（秒/毫秒/文本兼容）、`filter_messages`、`get_real_wechat_chats` / `get_real_wechat_messages` |
| REST 端点 | [L1019-L1627](file:///d:/code/chatlog-ai-portable/app/server.py#L1019-L1627) | 全部 HTTP 接口，详见 `docs/API_REFERENCE.md` |
| 静态挂载 + 入口 | [L1629-L1646](file:///d:/code/chatlog-ai-portable/app/server.py#L1629-L1646) | `/static` 挂载、`/` 返回 index.html、`main()`（启动两个引擎） |

> 行号说明：2026-09-11 新增「定时发送」后，上表中 **L513 之后**的原有区块整体下移约 410 行；上表已按新行号更新。

### 5.2 `app/adapters.py`（数据源适配，165 行）

- `DataSourceError`：适配层统一异常类型。
- `WeChatAdapter`：HTTP + Session，读 `/api/chat/accounts`、`/api/chat/sessions`、`/api/chat/messages`；**分页拉取**（每页最多 500 条，`order=desc` 后 `reversed` 还原时间正序）；去重靠 `id`。
- 统一返回结构：`list_chats()` → `{chats:[...], status_info:{...}}`；`list_messages()` → `List[Dict]`。
- 非文本消息转标签：微信 `renderType` → `[图片]/[语音]/[文件]...`。

### 5.3 `app/automation.py`（Windows 自动化，331 行）

- 仅 Windows。`ctypes` 调 `SendInput` 模拟按键 + 剪贴板注入。
- `press_hotkey(hotkey)`、`set_clipboard_text(text)`、`send_wechat_message(target_name, text, hotkeys, delays=None, active_chat_name=None)`。
- `STEP_DELAYS` 控制节奏：`open=0.8 / search=0.5 / select=0.8 / paste=0.25 / send=0.4` 秒。
- `send_wechat_message` 返回当前激活会话名，用于下次发送时判断是否已在目标会话（省去重复搜索）。
- `automation` 导入失败时 `server.py` 置 `AUTOMATION_AVAILABLE=False`，相关接口返回 400。

### 5.4 `app/launcher.py`（进程编排，560 行）

Job Object 管理子进程生命周期、端口探测、两阶段授权状态机、多账号选择、浏览器打开。

启动前会调用 `ensure_config_files()` 与 `ensure_directories()`（[L148-L175](file:///d:/code/chatlog-ai-portable/app/launcher.py#L148-L175)）补齐 `config/`，默认值来自 `config_defaults`（见 §7.7）。

### 5.5 `app/config_defaults.py`（默认值，30 行）

默认配置与默认提示词的**单一事实源**：`DEFAULT_CONFIG` / `DEFAULT_API_KEYS` / `DEFAULT_PROMPTS`。
纯数据模块，不导入任何业务模块，供 launcher（自动补齐）与 server（兜底）共用。

---

## 6. 前端结构（已完成第一步拆解）

### 6.1 拆分结果

| 文件 | 行数 | 内容 |
|---|---|---|
| [index.html](file:///d:/code/chatlog-ai-portable/app/static/index.html) | 215 | 仅 DOM 结构 + `<link>` + `<script src>` |
| [style.css](file:///d:/code/chatlog-ai-portable/app/static/style.css) | 227 | 全部样式（`:root` 变量、grid 布局、3 个媒体查询） |
| [app.js](file:///d:/code/chatlog-ai-portable/app/static/app.js) | 891 | 全部逻辑，59 个顶层函数（文件内共 63 处 `function` 声明，含 4 处嵌套） |

拆分前 `index.html` 为 1382 行（内联 `<style>` ≈250 行 + 内联 `<script>` ≈920 行）。

### 6.2 引用方式（**不要改错**）

```html
<link rel="stylesheet" href="/static/style.css">
...
<script src="/static/app.js"></script>
```

两个必须遵守的约束：

1. **必须是 `/static/...` 绝对路径**。页面挂在根路径 `/`，用相对路径 `./app.js` 会 404。静态目录在 [server.py L1030](file:///d:/code/chatlog-ai-portable/app/server.py#L1030) 通过 `app.mount("/static", StaticFiles(...))` 挂载。
2. **`<script>` 不能加 `type="module"`**。页面里有 **30 处 HTML 内联事件**（`onclick` / `oninput` / `onchange` / `onkeydown`），依赖函数挂在 `window` 全局上。一旦改用 ES Module，顶层函数不再挂 `window`，所有内联事件全部失效。

### 6.3 状态变量（app.js 顶部 L1-L12）

```
chats=[]  currentChat=null  rawMessages=[]  visibleMessages=[]
analysisType='general'  aiHistory=[]  messageLoading=false
autoReplyConfig=null  guardChatOptions=[]  providerPresets={}  providers=[]  activeProviderId=null
```

- `rawMessages` = 后端返回的原始消息；`visibleMessages` = 前端按时间/条数再次筛选后的结果。
  **两者分离**：切换筛选条件只重算 `applyFilters()`，不必重新请求后端。
- 发送给模型的始终是 `visibleMessages`。

### 6.4 入口与轮询（app.js L15-L27）

`window.onload` → `setupPaneSplitter()` → `toggleRangeControls()` → `loadProviders()` → `loadAutoReplyConfig()` → `loadChats(true)` → `refreshBalance()` → 无激活供应商则弹模型弹窗。
随后四组定时器：

| 周期 | 动作 |
|---|---|
| 10s | `loadChats(false)` 刷新会话列表 |
| 5s | `refreshMessages()` 刷新当前会话消息（页面隐藏且未开自动回复时跳过） |
| 3s | `refreshAutoReplyStatus()` 刷新引擎状态与日志 |
| 60s | `refreshBalance()` 刷新账户余额 |

### 6.5 函数清单（59 个顶层函数，按功能域）

| 功能域 | 函数 |
|---|---|
| 会话列表 | `loadChats` `renderChatList` `selectChat` `setSourceStatus` |
| 消息 | `getRangeConfig` `requestedLimit` `toggleRangeControls` `reloadMessages` `refreshMessages` `fetchMessages` `applyFilters` `renderMessages` |
| AI 分析 | `selectAnalysisType` `runPreset` `sendCustomPrompt` `handlePromptKey` `runAnalysis` `clearAiThread` `renderAiThread` `updateAnalysisCount` `formatHours` |
| 模型供应商 | `loadProviders` `updateModelStatus` `assistantName` `renderProviderList` `populateProviderTypeOptions` `onProviderTypeChange` `collectProviderForm` `validateProvider` `saveProvider` `activateProvider` `deleteProvider` `refreshBalance` `openModelModal` `closeModelModal` |
| 自动回复 | `loadAutoReplyConfig` `openAutoReplyModal` `closeAutoReplyModal` `applyAutoReplyConfigToForm` `loadGuardChatOptions` `renderGuardTargetList` `collectGuardTargets` `saveAutoReplyConfig` `updateAutoReplyStatus` `refreshAutoReplyStatus` `testHotkey` `testFullFlow` |
| 工具/其他 | `readJson` `apiError` `messageTimestamp` `shortTime` `renderMarkdown` `escapeHtml` `copyToClipboard` `copyHistoryItem` `setupPaneSplitter` `saveHistory` `shutdownAll` |

### 6.6 关键 DOM id（重写前端时需保留语义）

`sourceStatus` `sourceStatusText` `searchInput` `chatList` `chatTitle` `chatSubtitle` `timeMode` `timeValue` `timeUnit` `countMode` `countInput` `messageList` `paneSplitter` `modelStatus` `runButton` `analysisCount` `autoReplyStatus` `balanceStatus` `aiThread` `promptInput` `sendButton`
弹窗：`autoReplyModal` `arEnabled` `guardTargetList` `arContextCount` `arReplyPrompt` `hkOpen` `hkSearch` `hkPaste` `hkSend`；`modelModal` `providerList` `mpType` `mpName` `mpBaseUrl` `mpModel` `mpApiKey` `mpValidateBtn` `mpSaveBtn` `mpValidateMsg`

### 6.7 30 处内联事件（拆解时保留，重写时应改为事件绑定）

出现在 index.html 中，例如：`renderChatList()`、`toggleRangeControls()`、`runPreset()`、`sendCustomPrompt()`、`handlePromptKey(event)`、`validateProvider()`、`saveProvider()`、`onProviderTypeChange()`、`closeModelModal()` 等。重写前端时建议统一改为 `addEventListener`。

---

## 7. 配置文件与数据文件

**配置归属原则**：所有配置类文件统一收敛到根目录 `config/` 下；`data/` 只放运行时数据。除 `config/` 外，仓库任何位置都不得出现配置类文件（上游工具自写文件除外，见 §7.6）。

### 7.1 `config/config.json`（主配置）

| 字段 | 含义 |
|---|---|
| `port` | 本服务端口，默认 18765 |
| `wechat_api` | 微信数据源地址，默认 `http://127.0.0.1:10392` |
| `wechat_account` | 选定的微信账号（影响自动回复配置文件归属） |
| `active_provider` | 当前生效供应商 id（供应商明细见 `config/api-keys.json`） |

`config.example.json` 为脱敏示例（**入库**），键名与上表一致。
`config.json` 本身**不入库**：新环境首次启动时由 launcher 自动生成（见 §7.7）。

### 7.2 `config/api-keys.json`（**含明文 API Key**）

结构恒为 `{"providers": [...]}`，元素为 `{id, provider_type, name, base_url, model, api_key, balance_path, balance_parser}`。
读写函数：`load_api_keys` / `save_api_keys`（[server.py L75-L92](file:///d:/code/chatlog-ai-portable/app/server.py#L75-L92)）。
`id` 生成规则：`"prov-" + secrets.token_hex(5)`（见 [server.py L672](file:///d:/code/chatlog-ai-portable/app/server.py#L672)）。
本文件**不入库**，新环境首次启动时自动生成 `{"providers": []}`（见 §7.7）。

### 7.3 `config/prompts/*.txt`（提示词）

纯文本外置的全局静态提示词，读取函数 `load_prompt(name, fallback=None)`（[server.py L94-L103](file:///d:/code/chatlog-ai-portable/app/server.py#L94-L103)），文件缺失或为空时回退 `config_defaults.DEFAULT_PROMPTS` 中的同名单条默认文案。
本目录**不入库**，新环境首次启动时按 `DEFAULT_PROMPTS` 逐个生成（见 §7.7）；生成后用户可自由编辑，后续启动不会被覆盖。

| 文件 | 用途 |
|---|---|
| `summary-meeting.txt` | 会议纪要型总结 |
| `summary-todo.txt` | 待办事项型总结 |
| `summary-topics.txt` | 话题讨论型总结 |
| `summary-general.txt` | 通用型总结（默认） |
| `auto-reply-outer.txt` | 自动回复外层提示词（全局身份/主体） |
| `auto-reply-inner.txt` | 自动回复内层默认提示词（单会话口吻兜底） |
| `scheduled-greeting.txt` | 定时发送的打招呼提示词（先概括上下文，再写一条不突兀的招呼；2026-09-11 新增） |

### 7.4 `config/auto-reply/{account}.json`

自动回复配置，**按微信账号隔离**（账号名过滤掉非 `[A-Za-z0-9-_]` 字符，空则 `default`）。
结构：`{enabled, guard_targets[], context_count, reply_prompt, hotkeys{open_wechat,search,paste,send}}`。
`guard_targets[]` 元素：`{id, name, type: "friend"|"group", prompt}`。
与 `config/config.json` 分开存放，避免并发写冲突。本目录**不入库**（内含用户自定义 prompt，见 §7.7）。

### 7.4.1 `config/scheduled-send/{account}.json`（定时发送，2026-09-11 新增）

定时发送配置，**同样按微信账号隔离**（命名规则与自动回复完全一致：`_auto_reply_account_key()` 的复用）。
结构：`{enabled, tasks[]}`；`tasks[]` 元素字段与清洗规则见 `docs/API_REFERENCE.md` §6.5.1 / §6.5.2。

与自动回复的三个刻意差异：

| 项 | 自动回复 | 定时发送 | 为什么不同 |
|---|---|---|---|
| 快捷键 | 存在自己的配置里 | **复用** `load_auto_reply()["hotkeys"]` | 同一台机器同一套微信快捷键，存两份必然出现"改了一份忘了另一份" |
| 运行时状态 | 只存内存（指纹 / 签名） | 落盘 `data/scheduled-send-state.json` | 定时任务需要"今天已发过没有""待触发时刻"，重启丢状态会导致重复发送或重新摇随机数 |
| 状态存放位置 | 内存 `deque` | `data/` 下的独立文件 | 配置文件会被界面随时整份覆盖，状态混进去必然丢更新 |

`config/scheduled-send/` 已加入 `.gitignore`（含用户自定义 prompt，同 auto-reply）。

### 7.5 `data/summaries.json`（运行时数据，非配置）

`summaries{target_id: [aiHistory...]}`（会话 ID → AI 对话历史缓存）。
属于运行时数据而非配置，故保留在 `data/` 下；首次产生 AI 分析结果时才由 `save_summaries()` 创建（启动时不会预生成）。

### 7.6 上游工具自写文件（保持原位，勿迁）

| 路径 | 归属 |
|---|---|
| `data/wechat/output/account_keys.json` | 微信数据源自写（授权密钥，launcher 靠它判定是否已授权） |
| `data/wechat/output/` 其余内容 | 微信数据源导出的数据库与头像缓存 |

这些文件由上游程序按硬编码路径读写，迁移会破坏其读取逻辑，因此**不纳入本次整合范围**。

### 7.7 密钥 / 提示词不入库 + 启动时自动生成

**为什么**：`config/` 下的 `config.json`、`api-keys.json`、`prompts/*.txt`、`auto-reply/*.json` 要么含明文密钥，要么是用户可自由改写的提示词，**都不应提交到 git**；但程序又强依赖它们存在（例如 `require_paths()` 会校验 `config.json`）。因此采用"不入库 + 启动时补齐"的组合方案。

**单一事实源**：默认值集中在 [config_defaults.py](file:///d:/code/chatlog-ai-portable/app/config_defaults.py)，导出三个常量：

| 常量 | 对应文件 | 说明 |
|---|---|---|
| `DEFAULT_CONFIG` | `config/config.json` | 4 个键的默认主配置（`port` / `wechat_api` / `wechat_account` / `active_provider`） |
| `DEFAULT_API_KEYS` | `config/api-keys.json` | `{"providers": []}` |
| `DEFAULT_PROMPTS` | `config/prompts/<name>.txt` | 7 条默认提示词，键名即 `load_prompt(name)` 的 `name` |

该模块**只存放纯数据、不导入任何业务模块**，因此 launcher 与 server 都能安全引用，避免"两边各抄一份默认值"导致的漂移。

**生成时机**：launcher 启动流程中的 `ensure_config_files()`（[launcher.py L148-L166](file:///d:/code/chatlog-ai-portable/app/launcher.py#L148-L166)），位于 `ensure_directories()` 之后、`require_paths()` **之前**——顺序不可调换，否则新环境会因为 `config.json` 不存在而直接启动失败。

**幂等保证**：全部用 `if not path.exists()` 判断，**已存在的文件一律不覆盖**，因此用户改过的配置与提示词在后续启动中不会被冲掉。server.py 侧另有兜底（`load_config` 用 `DEFAULT_CONFIG`、`load_prompt` 用 `DEFAULT_PROMPTS`），即使不经 launcher 直接跑也不会崩。

**`.gitignore` 注意事项**：该文件历史上曾被误存为 **UTF-16 LE + BOM**，导致 git 把它当二进制、**全部规则静默失效**（`git check-ignore` 无任何匹配）。现已转回 UTF-8 无 BOM。忽略规则要点：

- 忽略 `/config/config.json`、`/config/config.json.bak`、`/config/api-keys.json`、`/config/*.tmp`
- 忽略 `/config/prompts/`、`/config/auto-reply/`
- 忽略 `/data/`、`/logs/`、`__pycache__/`、`*.py[cod]`
- **例外**：`/config/config.example.json` 必须入库，故未使用 `/config/*.json` 这类通配，而是逐条精确忽略。

> `config.json.bak` 是历史迁移遗留的备份文件名，当前目录下已不存在，但 `.gitignore` 仍保留该规则以防再次出现。

**验证方式**：新增忽略规则后务必用 `git check-ignore -v <path>` 逐个确认，并确认 `config/config.example.json` 返回未忽略（退出码 1）。

---

## 8. 关键机制

### 8.1 自动回复引擎（`AutoReplyEngine`，server.py L354-L498）

- 后台 daemon 线程，`POLL_INTERVAL=5.0s`，`DETECT_WINDOW=30` 条。
- 流程：`enabled` 且 `guard_targets` 非空 → 拉全量会话 → 对每个守护对象算「指纹」（`last_message|last_time|unread`），指纹不变则跳过 → 变化则拉最近 30 条消息 → 取最后一条非「我」的消息生成「签名」。
- **首轮只做基线（baselined），不回复**，避免启动瞬间对历史消息误回复。
- 签名变化才触发回复：`generate_reply_text()` 生成 → `automation.send_wechat_message()` 发送。
- 回复提示词是**两层**：外层 `reply_prompt`（全局身份/主体）+ 内层 `target.prompt`（单会话口吻），为空各有默认值。
- 日志用 `deque(maxlen=200)`，通过 `/api/auto-reply/status` 暴露。
- 仅在 Windows 且 `automation` 可导入时工作。

### 8.2 两级消息筛选（前后端各一次）

- 后端 `filter_messages(messages, recent_hours, recent_count)`（server.py L536-L547）按请求参数过滤。
- 前端 `applyFilters()`（app.js L207-L222）对返回结果再按 UI 上的时间/条数条件过滤。
- 结果：`/api/messages` 传 `recent_hours/recent_count` 是可选的；前端主要依赖自身 `applyFilters`。

### 8.3 适配器缓存

`get_wechat_adapter`（server.py L505）用 `_WECHAT_ADAPTER_KEY`（`wechat_api` 地址 + `wechat_account`）作为 key 缓存实例，配置变更自动重建。`AutoReplyEngine` 内部也独立缓存一个微信 adapter。

### 8.4 余额查询支持范围

只有 `balance_path` 非空的供应商支持：DeepSeek（`/user/balance`，parser=deepseek）、Kimi（`/users/me/balance`，parser=moonshot）。智谱、通义千问、自定义不支持。

---

### 8.5 定时发送引擎（`ScheduledSendEngine`，2026-09-11 新增，server.py L778-L922）

- 后台 daemon 线程，`POLL_INTERVAL=30s`。
- 流程：总开关打开且有启用任务 → 逐个任务判定 → 到点则取上下文（`context_count` 条）→ `generate_greeting_text()` 生成招呼 → `automation.send_wechat_message()` 发出。
- **触发模型 = 基础到期时刻 + 随机抖动**：`_scheduled_base_due()` 算出"应发时刻"（daily/weekly 按当天 HH:MM 且当天只发一次；interval 距上次满 N 分钟；once 到点且从未发过），首次可发时摇一个 `[0, jitter]` 的随机推迟量记为 `pending_fire_at` 落盘，之后每轮只判断是否已到该时刻——**重启不会重摇**。
- **冷却闸门**：距上次发送未满 `cooldown_minutes` 一律不发（对所有模式生效），此时清掉待触发时刻。
- **interval 模式首轮只写基线**（`last_sent_at = now`）不补发，与自动回复引擎"首轮只做基线"同一思路，避免刚启用就立刻发一条。
- **与自动回复共用 `_AUTOMATION_LOCK`**：键盘模拟是独占资源，两者同时触发会互抢剪贴板与前台窗口，结果是一条消息发到另一个会话。
- 状态落盘 `data/scheduled-send-state.json`，每轮只对发生变化的任务回写。
- 仅在 Windows 且 `automation` 可导入时工作；接口见 `docs/API_REFERENCE.md` §6.5。
- **已知代价**：到点会强制把微信窗口前置并模拟按键（`automation.ensure_wechat_foreground`），用户当时若在打字会被打断。这是键盘方案的固有代价，不是可绕过的 bug。

---

## 9. 端到端数据流（典型一次分析）

```
用户选会话
  → app.js loadChats()          GET  /api/chats
  → selectChat() → loadHistory() GET  /api/history?target_id=...
  → fetchMessages()             GET  /api/messages?...
  → applyFilters() → renderMessages()
用户点「生成分析」
  → runAnalysis()               POST /api/summarize  {target_name, messages, type, prompt, history}
  → server.get_active_provider() → _chat_completion() → 第三方模型 /chat/completions
  → 返回 {summary, analyzed_count}
  → aiHistory.push(assistant) → saveHistory()  POST /api/history
```

自动回复链路（后台，不经过前端）：
`AutoReplyEngine._poll_once` → `adapter.list_chats()` → 指纹比对 → `list_messages()` → `generate_reply_text()` → `automation.send_wechat_message()`。

定时发送链路（后台，不经过前端）：
`ScheduledSendEngine._poll_once` → 逐任务 `_process_task`（冷却闸门 → `_scheduled_base_due` → 摇抖动）→ `list_messages()` → `generate_greeting_text()` → `automation.send_wechat_message()` → 回写 `data/scheduled-send-state.json`。

---

## 10. 风险点与陷阱（改造前必读）

| # | 风险/陷阱 | 说明 |
|---|---|---|
| 1 | ES Module 化会打死内联事件 | 30 处 `onclick` 依赖全局函数，改 `type="module"` 前必须先把内联事件改为绑定 |
| 2 | 静态资源必须 `/static/...` | 相对路径 404 |
| 3 | `config/api-keys.json` 明文 API Key | 任何分享、截图、日志都必须脱敏；`save_provider` 只回传 masked |
| 4 | 编辑 provider 时 api_key 留空 = 沿用旧值 | 见 [server.py L655-L657](file:///d:/code/chatlog-ai-portable/app/server.py#L655-L657)，重写前端时不要误传空串 |
| 5 | `/api/shutdown` 触发 `os._exit(0)` | 无优雅关闭，且会同时结束两个服务（Job Object） |
| 6 | 自动化接口仅 Windows | 非 Windows 或导入失败时返回 400 |
| 7 | 消息上限 2000 条 | 微信每页 500 分页拼装，是性能/成本取舍，非数据库上限 |
| 8 | 时区/时间戳兼容 | `message_epoch` 同时处理秒、毫秒、文本时间，`>100000000000` 视为毫秒 |
| 9 | `config/`（除 `config.example.json`）、`runtime/`、`data/`、`logs/` 禁外发 | 含密钥、聊天缓存、日志；已由 `.gitignore` 忽略，但**提交前务必用 `git status` 复核** |
| 10 | 上游客户端版本敏感 | 微信解析器清单有效期、客户端升级可能导致取数失败（README「已验证版本」） |
| 11 | `.gitignore` 一旦变成 UTF-16 就会静默失效 | 编码错误时 git 视其为二进制、所有规则不生效且无任何报错；改动后必须 `git check-ignore -v` 验证 |
| 12 | 未授权时非管理员会直接退出 | [launcher.py L455-L463](file:///d:/code/chatlog-ai-portable/app/launcher.py#L455-L463)；首次使用必须"以管理员身份运行"，否则表现为"双击后闪退" |
| 13 | `wechatdb_broker.exe` 未纳入 Job Object | launcher 在 [L291-L300](file:///d:/code/chatlog-ai-portable/app/launcher.py#L291-L300) 用裸 `subprocess.Popen` 拉起，未 assign 到 Job，父进程退出后可能残留（它自带 `--parent-pid` 自杀兜底） |

---

## 11. 快速定位表（按需求找文件）

| 想改什么 | 去哪里 |
|---|---|
| 加一个 HTTP 接口 | `app/server.py`（REST 端点区 L1019 起） |
| 改数据源字段映射 | `app/adapters.py` |
| 改前端界面/交互 | **只有 .NET 客户端**（`client/ChatlogAI.Client/`，见 `docs/DOTNET_CLIENT_PLAN.md`）。原网页版 `app/static/*` 与 `app/launcher.py` 已于 2026-09-11 删除（§7.18）——**本文中所有关于网页版前端（`app/static/*`）与 `launcher.py` 的描述均已失效，仅作历史对照** |
| 改启动/端口/进程编排 | **客户端 Bootstrap**（`client/ChatlogAI.Client/Bootstrap/`，含 `StartupOrchestrator` / `PortGuard` / `JobObjectHost`）。原 `app/launcher.py` 已于 2026-09-11 删除，见 §7.18 |
| 改键盘模拟发送 | `app/automation.py` |
| 改供应商预设 | `app/server.py` 的 `PROVIDER_PRESETS` |
| 改自动回复默认值 | `app/server.py` 的 `AUTO_REPLY_DEFAULTS` |
| 改定时发送的触发规则/引擎 | `app/server.py` 的 `_scheduled_base_due` / `ScheduledSendEngine` |
| 改定时发送配置默认值 | `app/server.py` 的 `SCHEDULED_SEND_DEFAULTS` |
| 改默认配置 / 默认提示词 | `app/config_defaults.py`（自动生成的单一事实源，见 §7.7） |
| 改 git 忽略范围 | 根目录 `.gitignore`（必须 UTF-8，改完用 `git check-ignore -v` 验证） |
