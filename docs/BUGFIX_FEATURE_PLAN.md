# Chatlog AI 第二轮：Bug 修复 + 新功能 待办文档

> 用途：本文件是本轮「4 个 Bug 修复 + 4 个新功能」的**唯一待办事实源**。
> 新开对话窗口时，先读本文件即可恢复上下文，不必重新扫描代码。
> 建立时间：2026-10-29　当前分支：`ablation/dead-code-cleanup`（上一轮消融产出，尚未合并回 `feat/my`）。

---

## 0. 结论先行（已拍板，勿再询问）

| 决策项 | 结论 |
|---|---|
| 图片类 bug 修复范围 | **先只出调研结论**，不本次攻克 C6。产出独立调研报告，结论再定后续 |
| 知识文档关联机制 | **全文注入 + 截断**，不做检索/RAG |
| 知识文档大小约束 | 单文件上传 ≤ 4MB；关联文档累计 ≤ 4MB，超限提示「内容可能被截断」 |
| 文档格式 | md / txt / html / json（用户原文写「三种」但列了四种，按**四种**处理） |

---

## 1. Bug 根因与任务

### 1.1 Bug：图片无法显示（聊天框 / 聊天导出 / 朋友圈导出）

**根因（已确认，非简单代码 bug）**：自研数据链路的 **C6（图片本体解密）未完成**。

证据链：

1. [messages.py](file:///d:/code/chatlog-ai-portable/app/localdb/messages.py#L193-L195) 末尾 `image_md5` / `image_file_id` **恒留空**，注释明确「图片定位信息来自 message_resource.db，归 C6」。
2. [server.py](file:///d:/code/chatlog-ai-portable/app/server.py#L1566-L1580) 的 `with_image_url()`：`image_md5` / `image_file_id` 都空时直接 `return message`，因此消息不带 `image_url`，前端拿不到图。
3. `/api/media/image`（[server.py L1911](file:///d:/code/chatlog-ai-portable/app/server.py#L1911)）走的是 `get_wechat_adapter().fetch_media()`（**上游**），本地模式下上游已移除，图片链路彻底断。
4. 聊天导出 [ChatExporter.FormatHtml](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Services/ChatExporter.cs#L295-L309) 依赖 `imageDataUris`（base64），由调用方先下载；`image_url` 为空 → 图片退化成空链接。
5. 朋友圈导出 [exports.py](file:///d:/code/chatlog-ai-portable/app/localdb/exports.py#L25-L27) 明确「包里没有图片」：`.dat` 命名键与加密算法未知，正文用 `[图片 ×N]` 占位。

**本次任务（T-B1，纯调研，不写实现代码）**：

产出 `docs/C6_IMAGE_INVESTIGATION.md`，至少回答：

- `.dat` 文件的命名键如何推导（与 message_resource.db 的关系）；
- 图片本体加密算法是否与消息库同一套（AES-256-CBC + 页尾 IV，见 SELFHOST_DATA_PLAN.md §2.1）；
- `message_resource.db` 的 schema，`image_md5` / `image_file_id` 从哪里取；
- 结论：**能否攻克、代价、风险、是否值得单独排期**。

**验收**：报告能明确回答「本地图片这条链路到底能不能通、缺哪几块」，而不是「再研究研究」。

---

### 1.2 Bug：联系人「已删除好友」与「好友」混在一起，单查已删除好友无记录

**根因（已确认）**：[contacts.py](file:///d:/code/chatlog-ai-portable/app/localdb/contacts.py#L46-L54) 的 `_kind()` 只按 username 形态分类（`@chatroom`→group / `gh_`→official / `@openim`→enterprise / 其余→friend）。模块 docstring 明确：

- `include_former_friends` / `include_blocked` 开关**本地不生效**；
- `delete_flag` 实测好友里全是 0，无法作为判定依据。

**后果**：「已删除好友」没有独立形态特征、也没有可用字段 → 全被归进 `friend`；单查「已删除好友」时开关不生效 → 0 条。

**本次任务（T-B2）**：

1. **调研优先**：查清 `contact` 表完整 schema，找「已删除好友」的可靠判定字段（候选：`delete_flag` 的真实取值分布、`verify_flag`、`type`、`local_type` 的组合，或其他表）。
2. 若找到可靠字段 → 在 `_kind()` / 过滤逻辑中实现，并修正 `_counts()` 的 `formerFriends` / `blocked` 统计。
3. 若本地**确实无依据** → 向用户如实说明，决策：隐藏这两个开关，或回退上游。**不编造判定**。

**验收**：单查「已删除好友」要么返回正确列表，要么开关被移除/禁用且 UI 有明确说明，不能出现「勾了但静默无结果」。

---

### 1.3 Bug：「引用消息」无法正常显示

**根因（已确认）**：[messages.py](file:///d:/code/chatlog-ai-portable/app/localdb/messages.py#L59-L75) 的 `_appmsg_type()` 检测到 `<refermsg>` 就返回 `quote`，随后 `content = convert.type_label("quote")` → 只有 `[引用消息]` 一个标签，**没有解析被引用原文和回复正文**。

**本次任务（T-B3）**：

- 解析 appmsg 的 XML：`<refermsg>` 里取被引用者（`<displayname>`）与被引用原文（`<content>` 或 `<title>`），`<title>` 取回复正文。
- 组合成可读内容，例如：
  ```
  引用「张三」：<被引用原文>
  ——
  <自己的回复正文>
  ```
- 无正文时保留 `[引用消息]` 标签兜底，不破坏现有 `/api/summarize` 的 AI 输入口径（XML 仍不能直接当 content 流出）。

**验收**：引用消息在聊天框能看到「引用谁 + 原文 + 回复」，导出与 AI 输入不被 XML 污染。

---

## 2. 新功能与任务

### 2.1 功能：加载页实时展示日志 + 「确认进入」按钮（T-F1）

**现状**：[StartupWindow](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/StartupWindow.xaml.cs) 已有 `LogBox`，但 `Report()` 只追加**启动编排自身的进度**（阶段 0–5），不含子进程落盘的日志；且 `MarkCompleted()` 后直接交棒主界面，用户没机会停留看日志。

**日志文件位置**（[AppPaths.cs](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Bootstrap/AppPaths.cs#L74-L76) 的 `LogDir`）：

| 文件 | 内容 |
|---|---|
| `chatlog.log` | Python 后端 `server.py` 的 stdout（[StartupOrchestrator L468](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Bootstrap/StartupOrchestrator.cs#L468)） |
| `wechat.log` | `wechat-backend.exe` 的 stdout |
| `wechat-broker.log` | broker 的 stdout |

**任务**：

1. 在 `StartupWindow` 增加「实时 tail」：读取上述日志文件增量并追加到 `LogBox`（轮询或 `FileSystemWatcher`，注意文件可能尚未创建）。
2. 启动完成后**不自动关窗**，显示「确认进入」按钮；用户点击后才进入主界面，期间日志持续刷新、用户可自由滚动回看。

**验收**：启动期间 `LogBox` 能实时看到后端启动日志；完成后必须用户点「确认进入」才进主界面；查看日志不被阻断。

---

### 2.2 功能：设置里「查看运行日志」+ 导出日志（T-F2）

**现状**：[SettingsModuleView.xaml](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/Modules/SettingsModuleView.xaml) 无日志入口。

**任务**：

1. 设置模块新增「查看运行日志」按钮。
2. 弹出日志文件列表（`chatlog.log` / `wechat.log` / `wechat-broker.log`），用户选择后打开查看（只读）。
3. 支持「导出」选中日志文件（另存为到用户指定位置）。
4. 面向开发排查：日志查看窗口可支持按时间戳/关键字定位（此条作为可选增强，非本轮必做）。

**验收**：设置里能打开日志列表 → 选择 → 查看 → 导出，全程路径来自 `AppPaths.LogDir`，不写死。

---

### 2.3 功能：「关于」信息调整（T-F3）

**现状**：[AboutWindow.xaml](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/AboutWindow.xaml#L37-L45) 程序名硬编码 `Chatlog AI`；[AboutWindow.xaml.cs](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/Views/AboutWindow.xaml.cs#L30-L34) 版本从**程序集**读（`Assembly.GetName().Version`）。

**目标展示**：

| 项 | 值 |
|---|---|
| 开发者联系方式 | `2922977793@qq.com` |
| 程序版本 | 读 [client/VERSION](file:///d:/code/chatlog-ai-portable/client/VERSION)（当前 `3.1.0`） |
| 程序名 | `微信助手-ChatLog-Ai` |

**任务**：

1. 版本号改从 `client/VERSION` 文件读，替代程序集版本。
2. 程序名与联系方式写入 AboutWindow。
3. **待确认**：`client/VERSION` 打包后如何分发（当前 `AppPaths` 无 VERSION 相关路径；需确认它应落在 `RootDir` 下哪个位置并随安装包分发）。

**验收**：关于窗口显示三行且版本与 VERSION 文件一致（改 VERSION 文件即可改显示，无需重编译）。

---

### 2.4 功能：左侧「文档」模块（知识文档）（T-F4）

**目标**：左侧栏在「联系人」与「设置」之间加「文档」。用户可上传 md / txt / html / json，可删除；作为知识文档，在自动回复 / 定时发送时关联指定文档内容注入 prompt，避免 prompt 涉及面太窄。

#### 2.4.1 存储设计

| 项 | 值 |
|---|---|
| 文件存储 | `DataDir/docs/`（可写目录，与 `config/` 同级） |
| 元数据 | `DataDir/docs/index.json`：`{ id, filename, size, uploaded_at }[]` |
| 文件命名 | `<id>.<原始扩展名>`，id 用 `doc-<10位hex>`（与 provider / scheduled 一致） |
| 单文件上限 | ≤ 4MB（上传时校验，超限 400） |

#### 2.4.2 后端接口（新增）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/docs` | 文档列表（id / filename / size / uploaded_at） |
| POST | `/api/docs` | 上传（multipart 或 JSON，含 filename + content），≤4MB |
| GET | `/api/docs/{id}` | 读取单文档内容 |
| DELETE | `/api/docs/{id}` | 删除文档 |

> 契约字段与清洗规则以 `docs/API_REFERENCE.md` 为参照补充，本次需新增一节。

#### 2.4.3 关联注入设计（全文注入 + 截断）

1. 自动回复配置 `config/auto-reply/{account}.json` 与定时发送配置 `config/scheduled-send/{account}.json` 各新增 `doc_ids: []` 字段。
2. 生成回复时（`server.py` 的 `generate_reply_text` / `generate_greeting_text` / `_scheduled_generate`），读取关联文档内容，拼接进 **system prompt**。
3. **4MB 累计约束**：关联文档累计 > 4MB 时，**不静默**，返回/提示「内容可能被截断」，并截断到上限。
4. 注入位置刻意放在 system 层（身份/知识背景），不放 user 层，避免与「本次要求」混在一起。

#### 2.4.4 客户端

1. `MainWindow.xaml` 图标栏在「联系人」与「设置」之间插入「文档」RadioButton。
2. 新增 `DocsModuleView` / `DocsModuleViewModel`（列表 + 上传 + 删除）。
3. 自动回复窗口与定时发送编辑器新增「关联知识文档」选择器（可多选，显示累计大小与 4MB 余量）。
4. `MainViewModel` 增加 `IsDocsModule` 投影与 `Docs` 持有（复用现有模块挂法，见 [MainViewModel.cs](file:///d:/code/chatlog-ai-portable/client/ChatlogAI.Client/ViewModels/MainViewModel.cs) 现有 Contacts/GroupWatch 模式）。

**验收**：上传/删除可用；自动回复与定时发送能勾选文档，累计超 4MB 有提示；生成内容确实带入了文档知识。

---

## 3. 涉及文件速查

| 任务 | 后端 | 客户端 |
|---|---|---|
| T-B1 图片调研 | 只读，不写 | — |
| T-B2 已删除好友 | `app/localdb/contacts.py` | `ContactsModuleViewModel` / `ContactsModuleView`（若需隐藏开关） |
| T-B3 引用消息 | `app/localdb/messages.py`（+ `convert.py`） | — |
| T-F1 加载日志 | — | `StartupWindow` / `StartupOrchestrator`（日志 tail） |
| T-F2 查看日志 | — | `SettingsModuleView` + 新 ViewModel/Window |
| T-F3 关于 | — | `AboutWindow` / `AboutWindow.xaml.cs` / `AppPaths`（VERSION 路径） |
| T-F4 文档 | `app/server.py`（4 接口 + 注入 + 配置字段） | `MainWindow` / `DocsModuleView` / `DocsModuleViewModel` / 自动回复与定时发送编辑器 |

---

## 4. 风险与待确认点

| # | 事项 | 说明 |
|---|---|---|
| R1 | C6 调研可能结论为「不可行」 | 图片类 bug 可能最终无法在本地链路修复，需用户接受「上游模式才可显示图」或继续投入 |
| R2 | 「已删除好友」可能本地无依据 | 调研无字段时需用户决策：隐藏开关 or 回退上游，不能编造 |
| R3 | VERSION 分发位置未定 | T-F3 需确认 `client/VERSION` 打包后落点；当前 `AppPaths` 无此路径 |
| R4 | 文档注入 token 上限 | 4MB 截断是「字节」口径，实际还要受模型上下文窗口约束，截断粒度需在实现时定（按字符/按字节） |
| R5 | 当前分支状态 | 工作区在 `ablation/dead-code-cleanup`，上一轮消融两提交未合并；`feat/my` 尚有 477 行未提交 WIP。**本轮落地前需先厘清基线** |

---

## 5. 建议执行顺序

1. **T-B1（调研）** —— 最不确定，先出结论，避免后面白做。
2. **T-B2（调研 + 决策）** —— 同样有数据源不确定性，先定性。
3. **T-B3（引用消息）** —— 纯实现，根因清晰，可独立排。
4. **T-F3 / T-F2 / T-F1** —— 纯客户端，无数据源风险，可并行。
5. **T-F4（文档）** —— 涉及后端 + 客户端 + 配置，体量最大，放最后，前置依赖 T-B3 已理清 prompt 注入点。
