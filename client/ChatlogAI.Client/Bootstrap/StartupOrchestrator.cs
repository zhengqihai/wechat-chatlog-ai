using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 启动编排，等价 launcher.py 的 run()（L442-L546）。这是 .NET 客户端接管启动器职责的核心。
///
/// 阶段划分（与方案 §4 一致）：
///   阶段0 环境自检  ：建目录 / 补齐 config / 清退出标记 / 校验必需文件
///   阶段1 启动微信源：broker → wechat-backend.exe，等 10392 健康检查（上限 180s）
///   阶段2 授权判定  ：未授权则开浏览器授权页并原地等待密钥文件出现
///   阶段3 账号选择  ：多账号时问用户（替代原 input()）
///   阶段4 启动后端  ：python.exe app\server.py，等 18765 健康检查（上限 40s）
///   阶段5 交给界面  ：启动守护协程后返回，由 UI 接管
///
/// 与 Python 版的三处刻意差异（都写在对应代码处）：
///   1. 提权判定用 Environment.IsPrivilegedProcess（.NET 8 内置），不再 P/Invoke shell32；
///   2. broker 也被纳入 Job Object（补齐方案 R2）；
///   3. broker 的 stdout 落盘为 wechat-broker.log，而不是丢弃到 DEVNULL——
///      它现在归我们管了，出问题时必须能查。
/// </summary>
public sealed class StartupOrchestrator : IDisposable
{
    private const int WechatHealthTimeoutSeconds = 180;
    private const int ChatlogHealthTimeoutSeconds = 40;
    private const int HealthRequestTimeoutSeconds = 2;

    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BrokerStartupDelay = TimeSpan.FromSeconds(1);

    private readonly AppPaths _paths = AppPaths.Instance;
    private readonly IStartupInteraction _interaction;
    private readonly bool _forceInitialize;
    private readonly ConfigBootstrapper _bootstrapper;
    private readonly WechatAuthWatcher _authWatcher;
    private readonly WechatAccountEnumerator _accountEnumerator;
    private readonly JobObjectHost _job = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _childrenLock = new();
    private readonly List<ChildProcess> _children = new();

    /// <summary>
    /// 与后端通信共用同一个 HttpClient。
    ///
    /// 两个关键设置：
    ///   UseProxy=false —— 全部请求都指向 127.0.0.1，若走系统代理可能被劫持到代理服务器，
    ///                     表现为"本地服务明明起来了却连不上"。
    ///   Timeout=Infinite —— 超时改由每次请求自己的 CTS 控制。健康检查只需要 2s，
    ///                     而账号枚举需要 10s，用一个全局 Timeout 无法同时满足。
    /// </summary>
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private Task? _watchdog;
    private bool _disposed;

    public StartupOrchestrator(IStartupInteraction interaction, bool forceInitialize)
    {
        _interaction = interaction;
        _forceInitialize = forceInitialize;
        _bootstrapper = new ConfigBootstrapper(_paths);
        _authWatcher = new WechatAuthWatcher(_paths.WechatAccountKeysFile);
        _accountEnumerator = new WechatAccountEnumerator(_paths, _http);
    }

    /// <summary>
    /// 主循环检测到"该退出了"时触发，参数是给用户看的原因。
    /// 等价 launcher.py L513-L521 的 while 循环：退出标记文件出现，或任一子进程自己死了。
    /// </summary>
    public event Action<string>? ExitRequested;

    /// <summary>执行阶段 0-4。返回成功不代表已进入主界面——那是调用方（App）的事。</summary>
    public async Task<StartupResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 等价 authorize_needed = force_initialize or not has_wechat_key()。
            var authorizeNeeded = _forceInitialize || !_authWatcher.HasWechatKey();

            // 等价 Python 的 is_admin() 判定。原版是"非管理员就给一段提示然后退出"，
            // 因为读微信数据库必须有管理员权限。.NET 版已在 app.manifest 里声明
            // requireAdministrator，正常双击必然提权；这里的判定是兜底，
            // 防止 manifest 被绕过时表现为"点了没反应"（方案 R11）。
            if (authorizeNeeded && !Environment.IsPrivilegedProcess)
            {
                return StartupResult.Fail(
                    "检测到微信数据源尚未完成授权，而读取微信数据需要管理员权限。\n"
                    + "请关闭本程序，右键 Chatlog AI 图标选择“以管理员身份运行”后重试。");
            }

            // ---------- 阶段 0：环境自检 ----------
            _interaction.Report("正在检查 Chatlog AI 运行环境……");
            _bootstrapper.EnsureDirectories();
            _bootstrapper.EnsureConfigFiles();
            // 上一次异常退出可能残留退出标记，不清掉会让主循环立刻自杀。
            DeleteShutdownFile();
            _bootstrapper.RequirePaths();

            // ---------- 阶段 1：启动微信数据源 ----------
            PortGuard.EnsurePortFree(AppPaths.WechatPort);
            await StartWechatServiceAsync(cancellationToken).ConfigureAwait(true);

            // ---------- 阶段 2：授权判定 ----------
            if (authorizeNeeded)
            {
                var result = await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(true);
                if (result is not null)
                {
                    return result;
                }
            }
            else
            {
                _interaction.Report("已检测到微信数据源授权，直接启动分析服务。");
            }

            // ---------- 阶段 3：账号选择 ----------
            await PromptAccountSelectionAsync(cancellationToken).ConfigureAwait(true);

            // ---------- 阶段 4：启动 Python 后端 ----------
            PortGuard.EnsurePortFree(AppPaths.ChatlogPort);
            _interaction.Report("正在启动统一分析服务……");
            StartChatlogService();
            await WaitHttpAsync(
                $"{_paths.ChatlogUrl}/api/health",
                TimeSpan.FromSeconds(ChatlogHealthTimeoutSeconds),
                cancellationToken).ConfigureAwait(true);

            // ---------- 阶段 5：交给界面 ----------
            _interaction.SetAuthorizationPageAvailable(false);
            _interaction.Report($"本地服务已就绪：{_paths.ChatlogUrl}");
            StartWatchdog();
            return StartupResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return StartupResult.Cancelled();
        }
        catch (Exception ex)
        {
            // 等价 Python 的 except Exception：把原因和日志位置一起告诉用户，否则无从排查。
            return StartupResult.Fail($"{ex.Message}\n\n日志目录：{_paths.LogDir}");
        }
    }

    /// <summary>
    /// 停止全部子服务并释放资源。多次调用安全（幂等）。
    ///
    /// 内部一律 ConfigureAwait(false)：退出路径上可能被 OnExit 同步阻塞等待，
    /// 若续体要求回到 UI 线程，就会与"UI 线程正在等它"形成死锁。
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        if (_watchdog is not null)
        {
            try
            {
                await _watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        ChildProcess[] children;
        lock (_childrenLock)
        {
            children = _children.ToArray();
        }

        // 逆序停止：Python 后端依赖微信数据源，先停它才不会让后端抛一堆读库失败。
        for (var i = children.Length - 1; i >= 0; i--)
        {
            try
            {
                await children[i].StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // 单个进程清理失败不应阻断整体退出。
            }
        }

        // 关 job 句柄 = 内核无条件终止组内残余进程（含子进程自己派生的孙进程）。
        _job.Dispose();

        foreach (var child in children)
        {
            child.Dispose();
        }

        DeleteShutdownFile();
        _http.Dispose();

        try
        {
            _lifetime.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();

    // ---------- 阶段 1 ----------

    private async Task StartWechatServiceAsync(CancellationToken cancellationToken)
    {
        // broker 必须先于 backend 拉起：它是访问微信本地库的原生通道，backend 启动时会连它。
        if (File.Exists(_paths.WechatBrokerExe))
        {
            var broker = ChildProcess.Start(
                new[]
                {
                    _paths.WechatBrokerExe,
                    "--database-root", _paths.WechatDataDir,
                    "--parent-pid", Environment.ProcessId.ToString(),
                },
                _paths.WechatNativeDir,
                new Dictionary<string, string>(),
                _paths.LogFile("wechat-broker.log"),
                _job);

            AddChild(broker);
            // 等价 Python 的 time.sleep(1)：给 broker 一点时间完成初始化再拉 backend。
            await Task.Delay(BrokerStartupDelay, cancellationToken).ConfigureAwait(true);
        }

        _interaction.Report("正在启动微信实时数据服务……");

        var backend = ChildProcess.Start(
            new[] { _paths.WechatBackendExe },
            Path.GetDirectoryName(_paths.WechatBackendExe)!,
            BuildWechatEnvironment(),
            _paths.LogFile("wechat.log"),
            _job);

        AddChild(backend);

        await WaitHttpAsync(
            $"{_paths.WechatUrl}/api/health",
            TimeSpan.FromSeconds(WechatHealthTimeoutSeconds),
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>等价 build_wechat_env()（L265-L283）。</summary>
    private Dictionary<string, string> BuildWechatEnvironment()
    {
        // ProcessStartInfo.Environment 的起点就是当前进程的环境变量副本，
        // 所以这里只写"增量"，效果等价 Python 的 os.environ.copy() + update()。
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WECHAT_TOOL_HOST"] = "127.0.0.1",
            ["WECHAT_TOOL_PORT"] = AppPaths.WechatPort.ToString(),
            ["WECHAT_TOOL_DATA_DIR"] = _paths.WechatDataDir,
            ["WECHAT_TOOL_OUTPUT_DIR"] = _paths.WechatOutputDir,
            ["WECHAT_TOOL_UI_DIR"] = _paths.WechatUiDir,
            // 上游后端用它实现"父进程没了就自杀"，是我们 Job Object 之外的第二道保险。
            ["WECHAT_TOOL_DESKTOP_PARENT_PID"] = Environment.ProcessId.ToString(),
            ["WECHAT_TOOL_NATIVE_CORE_MODE"] = "required",
            ["WECHAT_TOOL_NATIVE_CORE_LIBRARY"] = _paths.WechatNativeLibrary,
            ["WECHAT_TOOL_NATIVE_CORE_BROKER"] = _paths.WechatBrokerExe,
            ["WCE_INTEGRITY_NATIVE_PATH"] = _paths.WechatIntegrityPyd,
            ["PYTHONIOENCODING"] = "utf-8",
        };

        // 便携包内置一份 ffmpeg，显式告知后端，避免依赖系统 PATH。
        if (File.Exists(_paths.WechatFfmpegExe))
        {
            environment["WECHAT_TOOL_FFMPEG"] = _paths.WechatFfmpegExe;
        }

        return environment;
    }

    // ---------- 阶段 2 ----------

    /// <summary>返回 null 表示授权已就绪、可以继续；否则返回应当终止的终态。</summary>
    private async Task<StartupResult?> EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        // 重新授权（--initialize）且密钥已存在：无法自动判定"扫描完成"，
        // 只能由用户看完页面后自己点确认——等价 Python 的 input("按回车继续")。
        if (_forceInitialize && _authWatcher.HasWechatKey())
        {
            _interaction.SetAuthorizationPageAvailable(true);
            OpenAuthorizationPage();

            const string message = "请在打开的页面中完成账号的重新扫描 / 更新，完成后点击“继续”。";
            _interaction.Report(message);

            var confirmed = await _interaction
                .ConfirmAsync(message, "继续", cancellationToken)
                .ConfigureAwait(true);

            return confirmed ? null : StartupResult.Cancelled();
        }

        _interaction.SetAuthorizationPageAvailable(true);
        OpenAuthorizationPage();
        _interaction.Report("正在等待微信授权完成，请保持微信客户端处于登录状态……");

        var authorized = await _authWatcher.WaitAsync(cancellationToken).ConfigureAwait(true);
        if (!authorized)
        {
            return StartupResult.Cancelled();
        }

        _interaction.Report("微信授权已完成，继续启动分析服务……");
        return null;
    }

    private void OpenAuthorizationPage()
    {
        try
        {
            // 全程序唯一保留浏览器的一步：授权页由上游 wechat-backend.exe 提供，无法原生化。
            Process.Start(new ProcessStartInfo(_paths.WechatUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // 打不开浏览器不是致命错误——界面上的"打开授权页面"按钮是同样的入口。
            _interaction.Report($"未能自动打开浏览器，请手动访问：{_paths.WechatUrl}");
        }
    }

    // ---------- 阶段 3 ----------

    /// <summary>等价 prompt_account_selection()（L397-L416）。</summary>
    private async Task PromptAccountSelectionAsync(CancellationToken cancellationToken)
    {
        var accounts = await _accountEnumerator.ListAsync(cancellationToken).ConfigureAwait(true);
        if (accounts.Count <= 1)
        {
            return;
        }

        var config = _bootstrapper.LoadConfig();
        var current = ReadConfigString(config, "wechat_account");

        var chosen = await _interaction
            .ChooseAccountAsync(accounts, current, cancellationToken)
            .ConfigureAwait(true);

        // 空串/null = 保持当前（等价 Python 版直接回车）。
        if (string.IsNullOrWhiteSpace(chosen) || string.Equals(chosen, current, StringComparison.Ordinal))
        {
            return;
        }

        config["wechat_account"] = chosen;
        ConfigBootstrapper.SaveJson(_paths.ConfigFile, config);
        _interaction.Report($"已选择微信账号：{chosen}");
    }

    private static string ReadConfigString(Dictionary<string, object?> config, string key)
    {
        if (!config.TryGetValue(key, out var value) || value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text.Trim(),
            System.Text.Json.JsonElement element => element.ValueKind == System.Text.Json.JsonValueKind.String
                ? (element.GetString() ?? string.Empty).Trim()
                : element.ToString().Trim(),
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    // ---------- 阶段 4 ----------

    private void StartChatlogService()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ★ 最关键的一条：便携 Python 的依赖全在 python-packages 里（无 .pth、无 site-packages），
            // 不注入 PYTHONPATH 会直接 "No module named fastapi"（方案 §3.2）。
            // Python 版是靠 .bat 在父进程里设好的，.NET 自己当父进程就必须自己设。
            ["PYTHONPATH"] = _paths.PythonPackagesDir,
            ["PYTHONUTF8"] = "1",
            ["PYTHONNOUSERSITE"] = "1",
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            // 程序文件只读、数据目录可写，必须把两个根目录都告诉后端（方案 §11.1）。
            ["CHATLOG_ROOT_DIR"] = _paths.RootDir,
            ["CHATLOG_DATA_DIR"] = _paths.DataDir,
            // 后端 /api/shutdown 靠 touch 这个文件通知启动器，客户端靠监听它感知退出。
            ["CHATLOG_SHUTDOWN_FILE"] = _paths.ShutdownFile,
        };

        var server = ChildProcess.Start(
            new[] { _paths.PythonExe, _paths.ServerScript },
            _paths.RootDir,
            environment,
            _paths.LogFile("chatlog.log"),
            _job);

        AddChild(server);
    }

    // ---------- 阶段 5：主循环 ----------

    private void StartWatchdog()
    {
        _watchdog = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    if (File.Exists(_paths.ShutdownFile))
                    {
                        RaiseExitRequested("收到界面退出请求，正在关闭全部本地服务……");
                        return;
                    }

                    foreach (var child in SnapshotChildren())
                    {
                        if (!child.IsRunning)
                        {
                            RaiseExitRequested(
                                $"本地服务异常退出（PID {child.Id}），日志目录：{_paths.LogDir}");
                            return;
                        }
                    }

                    await Task.Delay(WatchInterval, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void RaiseExitRequested(string reason)
    {
        // 事件可能没有订阅者（例如异常路径提前退出），此时忽略即可。
        ExitRequested?.Invoke(reason);
    }

    // ---------- 工具 ----------

    private async Task WaitHttpAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(HealthRequestTimeoutSeconds));

                using var response = await _http.GetAsync(url, timeoutSource.Token).ConfigureAwait(true);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 单次请求超时（2s）不算失败，服务可能只是还没开始监听。
                lastError = "请求超时";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(HealthPollInterval, cancellationToken).ConfigureAwait(true);
        }

        throw new InvalidOperationException($"服务启动超时：{url}（{lastError ?? "无响应"}）");
    }

    private void AddChild(ChildProcess child)
    {
        lock (_childrenLock)
        {
            _children.Add(child);
        }
    }

    private ChildProcess[] SnapshotChildren()
    {
        lock (_childrenLock)
        {
            return _children.ToArray();
        }
    }

    private void DeleteShutdownFile()
    {
        try
        {
            if (File.Exists(_paths.ShutdownFile))
            {
                File.Delete(_paths.ShutdownFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 与 Python 的 except OSError 对齐：清不掉也不该让启动失败。
        }
    }
}
