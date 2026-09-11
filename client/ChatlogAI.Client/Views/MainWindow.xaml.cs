using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ChatlogAI.Client.ViewModels;
using ChatlogAI.Client.Views.Modules;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 主界面窗口（T8 建，T16 重构）。结构是「56px 图标栏 + 内容宿主」，
/// 内容宿主里的五个模块各自是独立的 UserControl（见 Views/Modules/）。
///
/// 本文件只保留窗口级职责：
/// 1. 把 DataContext 指向 VM，并接上 VM 主动抛出的窗口级事件（提示 / 确认 / 首次启动引导）；
/// 2. 把窗口的最小化状态回写给 VM —— 它是后台轮询的开关判据之一
///    （对照 app.js 的 <c>document.hidden</c>）；
/// 3. 拦住"用户关窗"，交给 App 走"停服务 → 再退出"的完整路径。
///
/// 模块内的交互（定时任务编辑、导出、列表滚到底）已随代码搬进各自模块：
/// 它们只用到模块自己的控件与上下文，留在窗口里，窗口就会随模块数量一起膨胀。
/// VM 的生命周期仍由 App 持有（构造、InitializeAsync、Dispose 都在 App 里），
/// 窗口只负责"看"，不负责"生"与"杀"。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;

    /// <summary>本次启动的"查待确认账号切换"是否已经尝试过（只做一次）。</summary>
    private bool _pendingChecked;

    /// <summary>主窗口首帧是否已经画完。在此之前一律不许抢模态窗（见 <see cref="TryRunPendingCheck"/>）。</summary>
    private bool _firstFrameRendered;

    /// <summary>用户要求退出（点"退出"或关窗）。由 App 决定收尾时机，窗口自己不退出进程。</summary>
    public event Action? ExitRequested;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;

        // 主题已由 App.OnStartup 在任何窗口构造前应用好，这里只是把结果同步到按钮文案上。
        UpdateThemeButton();

        // 只订阅"必须由窗口做"的三件事。列表滚到底那两个事件（MessagesUpdated / AiThreadUpdated）
        // 归 ChatModuleView 订阅：它们操作的是模块内的控件，窗口既拿不到也不该知道。
        _viewModel.AlertRequested += OnAlertRequested;
        _viewModel.ConfirmRequested += OnConfirmRequested;
        _viewModel.ModelSettingsRequested += OnModelSettingsRequested;

        ContentRendered += OnFirstFrameRendered;
        Activated += OnActivated;

        StateChanged += OnStateChanged;
        Closed += OnClosed;
        Closing += OnClosing;
    }

    /// <summary>由 App 在完成服务清理后调用，此时关窗不再被理解为"用户要退出"。</summary>
    public void CloseForShutdown()
    {
        _closing = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        // 先拦下关闭，交由 App 走"停服务 → 再真正退出"的完整路径，
        // 否则窗口一关进程就结束，Job Object 虽然也能兜底，但会跳过友好清理。
        e.Cancel = true;
        ExitRequested?.Invoke();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // VM 比窗口活得久（App 在退出时才 Dispose 它），所以事件必须在这里摘掉，
        // 否则窗口已关闭后 VM 再抛事件，就会打到一棵已经脱离可视树的控件上。
        // 这份清单必须与构造器里的订阅一一对应；订阅方在别处的事件（MessagesUpdated /
        // AiThreadUpdated 由 ChatModuleView 自己退订）不要往这里加，
        // 两处各管一半最容易出现"改了订阅忘了改退订"。
        _viewModel.AlertRequested -= OnAlertRequested;
        _viewModel.ConfirmRequested -= OnConfirmRequested;
        _viewModel.ModelSettingsRequested -= OnModelSettingsRequested;

        ContentRendered -= OnFirstFrameRendered;
        Activated -= OnActivated;

        StateChanged -= OnStateChanged;
        Closed -= OnClosed;
        Closing -= OnClosing;
    }

    // ==================== 启动时查"账号切换待确认"（T19-B） ====================

    /// <summary>
    /// 主窗口首帧之后，查一次"是否有账号切换待确认"。
    ///
    /// 为什么必须排在**免责声明（T17）之后**：免责声明同样是"主窗口首帧后弹"的模态窗，
    /// 而 <c>ShowDialog</c> 是嵌套消息循环——两个模态窗一旦并发，后一个会把前一个盖住，
    /// 用户在两张对话框之间来回切。
    ///
    /// 怎么保证顺序（App.xaml.cs 不在本任务的改动清单里，不能在那里串起来）：
    /// App 那段用的是 <see cref="DispatcherPriority.Background"/>，这里刻意排到更低的
    /// <see cref="DispatcherPriority.ContextIdle"/> —— 等于"等它先跑"。它跑起来后会阻塞在自己的
    /// ShowDialog 里，此时我们被调度，就能"看见"那个已经打开的模态窗，先让位（见 TryRunPendingCheck），
    /// 等它关闭、主窗口重新 Activated 时再试一次。
    /// </summary>
    private void OnFirstFrameRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstFrameRendered;
        _firstFrameRendered = true;

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(TryRunPendingCheck));
    }

    /// <summary>模态窗关闭后主窗口会重新被激活——这正是"轮到我查待确认了"的信号。</summary>
    private void OnActivated(object? sender, EventArgs e) => TryRunPendingCheck();

    private void TryRunPendingCheck()
    {
        // 首帧之前不许抢：那时 App 的免责声明还没轮到，抢在它前面开窗就正好变成"两个模态窗并发"。
        if (_pendingChecked || !_firstFrameRendered || !IsLoaded)
        {
            return;
        }

        // 还有别的模态窗压在本窗口上（免责声明 / 首次启动的模型设置）：先不抢，
        // 等它关闭后本窗口重新 Activated 再进来。
        if (HasOwnedVisibleWindow())
        {
            return;
        }

        _pendingChecked = true;
        Activated -= OnActivated;
        _ = ShowPendingAccountWindowIfNeededAsync();
    }

    /// <summary>是否存在"Owner 是主窗口且仍可见"的窗口，即当前正压着主窗口的模态窗。</summary>
    private bool HasOwnedVisibleWindow()
        => Application.Current is { } app
           && app.Windows.OfType<Window>().Any(window => !ReferenceEquals(window, this)
                                                         && window.IsVisible
                                                         && ReferenceEquals(window.Owner, this));

    private async Task ShowPendingAccountWindowIfNeededAsync()
    {
        // 待确认就自动开窗并停在待确认区：否则用户只会看到"自动回复不工作"，
        // 却完全找不到原因（§7.17 客户端一节）。
        if (await _viewModel.CheckPendingSwitchAsync())
        {
            ModuleDialogs.ShowAccount(this, _viewModel);
        }
    }

    // ==================== 图标栏 ====================

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    /// <summary>
    /// 头像点击 → 「微信账号」窗口（T19-B）。
    ///
    /// 开窗与收尾（确认成功后清空重载）都收在 <see cref="ModuleDialogs.ShowAccount"/> 里：
    /// 设置模块的「管理」按钮走同一个入口，两处各写一份迟早分叉。
    /// </summary>
    private void OnAccountAvatarClick(object sender, RoutedEventArgs e)
        => ModuleDialogs.ShowAccount(this, _viewModel);

    /// <summary>
    /// 切换浅色 / 深色主题（T18）。
    ///
    /// 为什么不走 VM：主题是**窗口级/应用级**外观状态，不是业务数据；加一个
    /// <c>IsDarkTheme</c> 属性到 MainViewModel 只会让这个文件成为后续任务的改动热点。
    /// 图标栏这个按钮直接调 App 上的静态方法，谁都不必为此让路。
    ///
    /// 持久化在 <see cref="App.ToggleTheme" /> 里完成（切换与落盘必须成对，
    /// 分开写迟早出现"切了没存"）。
    /// </summary>
    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        App.ToggleTheme();
        UpdateThemeButton();
    }

    /// <summary>
    /// 把"当前主题"写进按钮文案与提示。
    ///
    /// 构造时必须调用一次：否则首次启动显示的会是 XAML 里写死的占位文案，
    /// 与实际生效的主题不一致（比如偏好是深色，按钮却写着别的）。
    /// 文案语义是**当前状态**而非目标状态，用户扫一眼图标栏就知道自己在哪个模式。
    /// </summary>
    private void UpdateThemeButton()
    {
        var isDark = App.IsDarkTheme;
        ThemeToggleButton.Content = isDark ? "深色" : "浅色";
        ThemeToggleButton.ToolTip = isDark ? "点击切换到浅色" : "点击切换到深色";
    }

    // ==================== VM 事件 → View 动作 ====================

    /// <summary>
    /// VM 不引用 MessageBox（那是 View 的职责），需要提示时改抛这个事件。
    /// 该事件只会在用户点击"生成分析/发送"后触发，此时窗口必然已经 Show 过，
    /// 因此可以用 <c>this</c> 作为 owner 让弹框正确居中在应用之上。
    /// </summary>
    private void OnAlertRequested(string message)
        => MessageBox.Show(this, message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// 需要一次"是/否"确认（对应原前端的 <c>window.confirm</c>）。
    /// 目前只有"删除定时发送任务"用到，默认按钮给 Cancel 更安全。
    /// </summary>
    private bool OnConfirmRequested(string message)
        => MessageBox.Show(
               this,
               message,
               "Chatlog AI",
               MessageBoxButton.OKCancel,
               MessageBoxImage.Question,
               MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void OnStateChanged(object? sender, EventArgs e)
        => _viewModel.IsWindowVisible = WindowState != WindowState.Minimized;

    /// <summary>
    /// VM 在 InitializeAsync 末尾发现"没有生效供应商"时会抛 ModelSettingsRequested（对照 app.js 的
    /// <c>if (!activeProviderId) openModelModal()</c>），把首次启动的用户直接送到配置入口。
    ///
    /// 必须用 Dispatcher 排到本次调用栈之后：这个事件是在 InitializeAsync 内部同步抛出的，
    /// 此刻它还没返回，直接 ShowDialog() 会把紧随其后的 StartPolling() 一起堵到弹窗关闭为止——
    /// 表现就是"弹窗开着的时候消息列表、自动回复状态、余额全都不刷新"。
    ///
    /// 延后调度留在窗口这一层，而不是下沉进 ModuleDialogs：这是"VM 事件回调栈里不能开模态窗"的
    /// 窗口级约束，弹窗本身不该知道调用它的是事件还是按钮点击。
    /// </summary>
    private void OnModelSettingsRequested()
        => Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => ModuleDialogs.ShowModelSettings(this, _viewModel)));
}
