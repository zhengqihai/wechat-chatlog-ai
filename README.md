# Chatlog AI

A Windows desktop app that reads the WeChat chat history already stored on your machine and puts it to work with AI — summaries, action items, Q&A, auto-replies, and scheduled messages. Everything runs locally on your own computer.

Chatlog AI starts a local service that reads WeChat's local (encrypted) database files and shows your real conversations in a native desktop client. Pick a conversation, set a time / message range, then send the filtered text to an AI model to summarize, extract action items, or continue a Q&A thread. It also ships auto-reply and scheduled-send engines driven by the same model.

## Features

- **Chat browser** — list real WeChat conversations, view messages by time and count range.
- **AI analysis** — summarize, extract meeting notes / action items / topics, or ask follow-up questions against the currently filtered messages.
- **Model providers** — DeepSeek, Kimi (Moonshot), Zhipu GLM, Qwen (DashScope), and any OpenAI-compatible custom endpoint. Models and API keys are configured in the client.
- **Auto-reply** — monitor chosen conversations and auto-answer new messages (optional).
- **Scheduled send** — send a message on a schedule (`daily` / `weekly` / `interval` / `once`).
- **Contacts** — browse and export your WeChat contacts.
- **Moments (朋友圈)** — view your timeline and export it.
- **Knowledge documents** — upload `md` / `txt` / `html` / `json` files and inject them into auto-reply / scheduled-send prompts.
- **Anti-recall** — surface messages that were recalled by the sender.
- **Chat export** — export the current conversation as Markdown / HTML / JSON / CSV.

## How it works

| Layer | Technology | Role |
|---|---|---|
| Desktop client | .NET 8 (WPF) | The only UI; also orchestrates startup/teardown of the local services. |
| Backend | Python (FastAPI) | REST API, AI orchestration, auto-reply / scheduled-send engines, Windows keyboard automation. |
| Local data layer | Python (`app/localdb/`) | Reads WeChat's encrypted SQLite databases directly from your machine. |
| Runtime | Bundled portable Python | Ships with the app, so end users need no Python install. |

Flow:

```
[WPF client] ──HTTP 127.0.0.1──▶ [FastAPI backend] ──▶ [local data layer] ──▶ WeChat DB files
                                        │
                                        └──────────────────────────────▶ [LLM provider API]
```

The backend listens only on `127.0.0.1` and is not exposed to the network.

## Data access & privacy

- Chatlog AI **reads** WeChat's local database files directly. It **does not** modify your WeChat database.
- Reading requires a per-account database key (`db_key`). This key is bound to your WeChat account and device — it cannot be copied from another machine and is never part of a release.
- Chatlog AI **does not** extract the key itself and **does not** bypass WeChat's login or device authorization. You must already have WeChat installed and logged in on the same machine, with chat history present locally.
- Non-text messages (images, voice, video, files, …) are represented as type labels in AI analysis; no OCR or speech recognition is performed.
- **Please note:** if you enable **auto-reply** or **scheduled send**, the app genuinely sends messages in WeChat by simulating keyboard input. That is what those two features do by definition. Verify each guarded conversation and scheduled task before enabling them, and watch the on-screen send status.
- Only when you explicitly run an analysis or ask a question is the currently filtered chat text sent to the model API address of the active provider.

Sensitive data (database keys, chat caches, logs, model API keys) is stored under `%ProgramData%\ChatlogAI` and is intentionally excluded from the repository (see `.gitignore`).

## Installation

1. Install and log in to WeChat, and make sure the machine already has chat history.
2. Download the latest installer (`ChatlogAI-Setup-<version>.exe`) from the [Releases page](https://github.com/zhengqihai/wechat-chatlog-ai/releases) and run it. Choose the install location and, optionally, a desktop shortcut.
3. Launch **Chatlog AI** from the Start menu or desktop shortcut. Reading WeChat data requires administrator rights; run the app as administrator.
4. On first run, import the database key (below) and configure a model provider + API key.

## First-time setup (importing the database key)

Chatlog AI needs your WeChat `db_key` to decrypt the local database. This is a one-time, per-account step:

1. Obtain the `db_key` for your WeChat account using a compatible third-party key-extraction tool.
2. In the client, open the **WeChat Account** window and paste the key into the "WeChat database key" section.
3. The client verifies the key by actually decrypting the first page of your local database before accepting it. Invalid keys are rejected and are not saved.

The key is stored locally in `%ProgramData%\ChatlogAI\config\wechat-keys.json`. Treat it as a credential — do not share it. If you change accounts, change your password, re-login the device, or reinstall WeChat, you will need to import the key again.

## Model configuration

Providers and API keys are configured entirely in the client — no manual config-file editing.

1. Open **Settings → Model Settings** (or the dialog that appears on first launch).
2. Pick a provider type (DeepSeek / Kimi / Zhipu GLM / Qwen / Custom); the Base URL and default model are filled in automatically.
3. Enter your API key and click **Validate**. The app makes a real call to confirm the key before saving.
4. Add multiple providers and click **Use** to switch the active one. Analysis and auto-reply always use the active model.
5. **Refresh balance** queries the current account balance (supported for DeepSeek and Kimi).

Saved keys are written to `config/api-keys.json` and only shown masked in the UI. Do not share that file.

## Building from source

Prerequisites:

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6.3+](https://jrsoftware.org/isdl.php)

Build the installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\client\build-installer.ps1
```

This runs `dotnet publish` (Release / win-x64 / self-contained), assembles a staging directory from `app\` and `runtime\`, runs a hard security gate (the build aborts if any key, chat data, log, or bytecode cache is found in the package), and finally compiles the installer with Inno Setup.

Output: `client\build\dist\ChatlogAI-Setup-<version>.exe`

Useful switches:

- `-SkipPublish` — reuse the previously published client (debugging the packaging flow only).
- `-SkipInstaller` — stop after staging + security checks (no Inno Setup needed).
- `-Version <x.y.z>` — override the version (default reads `client\VERSION`).

## Project structure

```
app/                     Python backend (FastAPI)
  server.py              REST API + business logic (analysis, auto-reply, scheduled send)
  adapters.py            data-source adapter (upstream HTTP / local)
  automation.py          Windows keyboard & clipboard automation
  config_defaults.py     single source of truth for default config + prompts
  docs.py                knowledge-document module
  localdb/               self-hosted local WeChat database reader (no upstream dependency)
client/                  .NET 8 WPF desktop client
  ChatlogAI.Client/      UI, view models, startup orchestration (Bootstrap/)
  installer/             Inno Setup script (ChatlogAI.iss)
  build-installer.ps1    one-command packaging
  VERSION                version source of truth
runtime/                 bundled portable runtimes (Python + third-party packages)
config/                  user config — generated at first run, not committed (see .gitignore)
docs/                    design & API reference documents
licenses/                third-party license texts
```

## Verified environment

- Windows 10/11 x64
- WeChat: Weixin 4.1.12.26
- .NET 8 desktop runtime
- Python 3.11 (bundled portable runtime)

WeChat upgrades can change the database layout or key handling, so newer WeChat versions may require updates to the local data layer.

## Documentation

- `docs/SELFHOST_DATA_PLAN.md` — design of the self-hosted local database-reading layer.
- `docs/API_REFERENCE.md` — full HTTP API and data-model reference.
- `docs/DOTNET_CLIENT_PLAN.md` — desktop client architecture and implementation notes.
- `docs/C6_IMAGE_INVESTIGATION.md` — status of the local image-decryption chain.

## License & third-party notices

This repository bundles or derives from third-party components. See `licenses/` for the full texts:

- **Python** — PSF License (`licenses/Python/LICENSE.txt`).
- **WeFlow** — CC BY-NC-SA 4.0 (`licenses/WeFlow/LICENSE.txt`).

## Disclaimer

Chatlog AI only processes data you legally own or are authorized to access. It does not bypass WeChat's login or device authorization. Please comply with the software license, platform terms of service, and the laws of your jurisdiction. Use of the auto-reply and scheduled-send features is at your own risk.
