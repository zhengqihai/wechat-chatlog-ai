using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ChatlogAI.Client.Bootstrap;
using ChatlogAI.Client.Services;
using ChatlogAI.Client.ViewModels;
using ChatlogAI.Client.Views;

namespace ChatlogAI.Client;

/// <summary>
/// 应用入口，接管原 launcher.py 的职责：先完成环境与子服务编排，再展示原生界面。
///
/// 生命周期由显式调用 <see cref="Application.Shutdown"/> 控制（见 App.xaml 的 ShutdownMode），
/// 因为启动阶段需要"先关启动窗口、再开主窗口"，而这两个动作之间会有一瞬间没有任何窗口，
/// 若用默认的 OnLastWindowClose，进程会在那一刻自己退出。
/// </summary>
public partial class App : Application
{
    /// <summary>浅色颜色字典的 pack URI。pack:// 三斜杠后是 assembly 名，这里指向自身。</summary>
    private static readonly Uri LightThemeSource =
        new("pack://application:,,,/Themes/Theme.Light.xaml", UriKind.Absolute);

    /// <summary>深色颜色字典的 pack URI。</summary>
    private static readonly Uri DarkThemeSource =
        new("pack://application:,,,/Themes/Theme.Dark.xaml", UriKind.Absolute);

    /// <summary>
    /// 颜色字典里必然存在的 key，用来在 <see cref="Application.Resources" /> 的
    /// MergedDictionaries 中认出"哪一项是颜色字典"。
    /// 为什么不在 C# 里塞一个标记 key 进去：那会成为第三个需要与两份 XAML 保持同步的东西；
    /// 直接探一个真实存在的 key，漏了会立刻在编译期后第一次启动就暴露。
    /// 注意不能用 Theme.xaml 里也有的 key 来探（如 BoolToVis），它不区分两份字典。
    /// </summary>
    private const string ThemeProbeKey = "PageBrush";

    private StartupOrchestrator? _orchestrator;
    private StartupWindow? _startupWindow;
    private MainWindow? _mainWindow;
    private ChatlogApiClient? _apiClient;
    private MainViewModel? _mainViewModel;

    /// <summary>
    /// 主界面阶段的取消源。启动阶段的取消由 <c>StartupWindow.CancellationToken</c> 承担，
    /// 主界面阶段没有那一层，退出时如果不打断在途请求，就会出现"服务已经关完、
    /// 界面还在轮询"的无效请求，续体回来时 Dispatcher 可能已经关闭。
    /// </summary>
    private readonly CancellationTokenSource _mainLifetime = new();

    private bool _exiting;

    /// <summary>客户端本地偏好（主题 / 免责声明不再提示）。进程级唯一实例，见 ClientSettings。</summary>
    public static ClientSettings Settings { get; private set; } = new();

    /// <summary>当前是否为深色主题。窗口用它决定按钮文案等内容。</summary>
    public static bool IsDarkTheme { get; private set; }

    /// <summary>
    /// 切换主题：**整体替换** MergedDictionaries 里的颜色字典那一项。
    ///
    /// 为什么不 Clear() 后重加：MergedDictionaries 里还有 Theme.xaml（转换器 + 控件样式），
    /// Clear 会把它们一起清掉，所有控件瞬间退化成 Aero2 默认外观——按钮、TextBox、
    /// ComboBox 会当场变形。
    ///
    /// 为什么不需要重建控件：样式里的颜色全是 DynamicResource，替换字典会触发
    /// 资源失效通知，WPF 自动重新求值。这是 T15 把颜色全部收拢到具名 Brush 留下的红利。
    ///
    /// 已知边界：已经渲染出来的 FlowDocument（AI 回答）不会跟着换色——它的 Run.Foreground
    /// 是渲染那一刻取到的**快照**。取舍是"只让新生成的回答变色"，重建所有历史文档
    /// 代价大且会撞上"文档已有父级"的问题（见 docs/DOTNET_CLIENT_PLAN.md §14.6.3）。
    /// </summary>
    public static void ApplyTheme(bool dark)
    {
        var merged = Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = dark ? DarkThemeSource : LightThemeSource };

        for (var i = 0; i < merged.Count; i++)
        {
            if (!merged[i].Contains(ThemeProbeKey))
            {
                continue;
            }

            merged[i] = replacement;
            IsDarkTheme = dark;
            return;
        }

        // 兜底：App.xaml 里那一项本该总在。真走到这里说明资源被人改坏了，
        // 插到最前面也要保证颜色可用，而不是让整个界面裸奔。
        merged.Insert(0, replacement);
        IsDarkTheme = dark;
    }

    /// <summary>在当前主题与另一套之间切换，并立即落盘。返回切换后的状态（true = 深色）。</summary>
    public static bool ToggleTheme()
    {
        ApplyTheme(!IsDarkTheme);
        Settings.IsDarkTheme = IsDarkTheme;
        Settings.Save();
        return IsDarkTheme;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 主题必须在任何窗口构造之前定下来：窗口一 Show 就会渲染，
        // 若先亮浅色再换成深色，用户会看到一次明显的闪跳。
        Settings = ClientSettings.Load();
        ApplyTheme(Settings.IsDarkTheme);

        // 等价 launcher.py 的 --initialize：强制重新进入授权阶段，用于更换/刷新账号密钥。
        // 原「首次初始化.bat」退役后，这个入口由安装包与桌面快捷方式承担（方案 T12/T14）。
        var forceInitialize = e.Args.Any(
            arg => string.Equals(arg, "--initialize", StringComparison.OrdinalIgnoreCase));

        _startupWindow = new StartupWindow();
        _startupWindow.Show();

        _orchestrator = new StartupOrchestrator(_startupWindow, forceInitialize);
        _orchestrator.ExitRequested += OnExitRequested;

        var result = await _orchestrator.RunAsync(_startupWindow.CancellationToken).ConfigureAwait(true);

        if (!result.Success)
        {
            // 失败/取消都要把已经拉起来的子服务收干净，否则会残留进程占端口，下次启动直接报"端口被占用"。
            await _orchestrator.ShutdownAsync().ConfigureAwait(true);

            _startupWindow.MarkCompleted();
            _startupWindow.Close();
            _startupWindow = null;

            if (!result.IsCancelled && result.ErrorMessage is { } message)
            {
                MessageBox.Show(message, "Chatlog AI 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown(result.IsCancelled ? 0 : 1);
            return;
        }

        // 阶段 5：交棒给主界面。必须先 Show 新窗口再关旧窗口——
        // 中间任何一刻"零窗口"都可能影响关闭语义与任务栏图标。
        _apiClient = new ChatlogApiClient();
        _mainViewModel = new MainViewModel(_apiClient);

        _mainWindow = new MainWindow(_mainViewModel);
        _mainWindow.ExitRequested += () => _ = RequestExitAsync();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        // T17：免责声明要等主窗口首帧渲染之后再弹，理由见 OnMainWindowFirstFrame。
        _mainWindow.ContentRendered += OnMainWindowFirstFrame;

        _startupWindow.MarkCompleted();
        _startupWindow.Close();
        _startupWindow = null;

        // 界面先亮出来、再拉数据：InitializeAsync 里虽然刻意不 await 余额查询，
        // 但会话/模型配置这几步仍要走本地 HTTP；在 Show 之前 await 会让用户对着空屏等。
        // 失败不弹框——VM 内部已经把错误写进各状态栏文案（SourceStatus / ModelStatus），
        // 这里再弹一次只会和状态栏重复。
        await _mainViewModel.InitializeAsync(_mainLifetime.Token).ConfigureAwait(true);
    }

    /// <summary>
    /// 主窗口首帧渲染完成时弹免责声明（T17）。
    ///
    /// 时机为什么是这里：
    /// 1. 不能放在 StartupWindow 之前。它是纯告知，不该阻塞"启动 → 起服务"这条链路，
    ///    更不该让用户以为程序卡在一张同意页上（§14.5.2）。
    /// 2. 不能紧跟 Show() 之后同步 ShowDialog。Show() 只是把窗口排进渲染队列，
    ///    此刻界面还没画出来，弹框会盖在一片空白上。
    ///    ContentRendered 正是"第一帧已经画完"的信号。
    /// 3. 用 Dispatcher 排到 Background 优先级，而不是在事件里直接 ShowDialog：
    ///    让 OnStartup 里紧随 Show 之后的 <c>InitializeAsync</c> 先跑起来，
    ///    否则"界面先亮出来、再拉数据"的顺序会被弹框顶到前面去。
    ///    ShowDialog 内部是嵌套消息循环，期间的轮询与续体照常调度，不会冻住界面。
    ///
    /// 订阅是一次性的：窗口比这里活得久，常驻订阅会在每次重绘后重复弹框。
    /// </summary>
    private void OnMainWindowFirstFrame(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.ContentRendered -= OnMainWindowFirstFrame;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                // _mainWindow 在退出路径里会被清空；此处再取一次并在方法内判空，
                // 避免"首帧回调还没执行，用户已经点了退出"时对着已关闭的窗口开模态框。
                if (_mainWindow is { } owner)
                {
                    DisclaimerWindow.ShowIfNeeded(owner);
                }
            }));
    }

    /// <summary>主循环（守护协程）发现"该退出了"时的回调，可能来自线程池线程。</summary>
    private void OnExitRequested(string reason)
    {
        _startupWindow?.Report(reason);
        Dispatcher.BeginInvoke(new Action(() => _ = RequestExitAsync()));
    }

    /// <summary>统一的退出路径：停全部子服务 → 关窗口 → 结束进程。</summary>
    private async Task RequestExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // 先停主界面的定时器与在途请求，再停子服务：
        // 反过来的话，服务已经关掉，界面每 5 秒仍会打一次注定失败的请求。
        _mainLifetime.Cancel();
        _mainViewModel?.Dispose();
        _mainViewModel = null;

        if (_orchestrator is not null)
        {
            await _orchestrator.ShutdownAsync().ConfigureAwait(true);
        }

        _apiClient?.Dispose();
        _apiClient = null;

        _mainWindow?.CloseForShutdown();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：无论从哪条路径退出（含未处理异常、系统注销），都保证子服务被清理。
        // ShutdownAsync 内部全部 ConfigureAwait(false)，因此在 UI 线程同步等待不会死锁。
        _mainLifetime.Cancel();
        _mainViewModel?.Dispose();
        _apiClient?.Dispose();
        _mainLifetime.Dispose();

        _orchestrator?.ShutdownAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "Chatlog AI 发生未处理异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // 标记已处理，避免 WPF 立即崩溃退出；随后走统一退出路径做清理。
        e.Handled = true;
        _ = RequestExitAsync();
        Shutdown(1);
    }
}
