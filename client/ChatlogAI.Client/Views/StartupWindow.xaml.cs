using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using ChatlogAI.Client.Bootstrap;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 启动进度窗口，同时充当 <see cref="IStartupInteraction"/> 的 WPF 实现。
///
/// 它存在的意义：原 launcher.py 是靠控制台 print + input() 与用户交互的。
/// 去掉控制台之后，"正在干什么"（进度）与"需要你干什么"（授权确认、账号选择）
/// 必须有一个可视化落点，否则用户面对的是一个点了没反应的程序。
///
/// 之所以让 View 直接实现接口、而不是再套一层 ViewModel：
/// 这个窗口的生命周期只有"启动这几秒"，状态极简（一个进度文案 + 一个日志框 + 三个按钮），
/// 引入 MVVM 只会增加文件数而不带来可测试性收益。等主界面（T8）有真实数据流时再上 MVVM。
/// </summary>
public partial class StartupWindow : Window, IStartupInteraction
{
    private readonly CancellationTokenSource _cancellation = new();

    private TaskCompletionSource<bool>? _confirmSource;
    private CancellationTokenRegistration _confirmRegistration;

    private TaskCompletionSource<string?>? _accountSource;
    private CancellationTokenRegistration _accountRegistration;

    private bool _completed;

    public StartupWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>启动编排的取消令牌；用户点"退出"或关窗即触发。</summary>
    public CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>
    /// 启动成功后由 App 调用。之后关闭本窗口只是"交棒给主界面"，
    /// 不再被 <see cref="OnClosing"/> 解读为"用户取消启动"。
    /// </summary>
    public void MarkCompleted() => _completed = true;

    // ---------- IStartupInteraction ----------

    public void Report(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(new Action(() => Report(message)));
            return;
        }

        StatusText.Text = message;
        AppendLog(message);
    }

    public void SetAuthorizationPageAvailable(bool available)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(new Action(() => SetAuthorizationPageAvailable(available)));
            return;
        }

        OpenAuthButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    public Task<bool> ConfirmAsync(string message, string confirmButtonText, CancellationToken cancellationToken)
    {
        void Show()
        {
            AppendLog(message);
            StatusText.Text = message;

            _confirmSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 编排层被取消（用户点退出/关窗）时，同步解除等待，避免 RunAsync 永远挂住。
            _confirmRegistration = cancellationToken.Register(
                () => Dispatcher.BeginInvoke(new Action(() => ResolveConfirm(false))));

            ConfirmButton.Content = confirmButtonText;
            ConfirmButton.Visibility = Visibility.Visible;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(new Action(Show));
        }
        else
        {
            Show();
        }

        return _confirmSource!.Task;
    }

    public Task<string?> ChooseAccountAsync(
        IReadOnlyList<WechatAccount> accounts,
        string currentAccount,
        CancellationToken cancellationToken)
    {
        void Show()
        {
            AccountList.ItemsSource = accounts;
            AccountList.SelectedItem = accounts.FirstOrDefault(item => item.Account == currentAccount)
                                      ?? accounts[0];
            AccountHintText.Text = currentAccount.Length == 0
                ? "当前尚未配置账号。"
                : $"当前使用：{currentAccount}";

            _accountSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _accountRegistration = cancellationToken.Register(
                () => Dispatcher.BeginInvoke(new Action(() => ResolveAccount(null))));

            AccountPanel.Visibility = Visibility.Visible;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(new Action(Show));
        }
        else
        {
            Show();
        }

        return _accountSource!.Task;
    }

    // ---------- 事件处理 ----------

    private void OnOpenAuthorizationPageClick(object sender, RoutedEventArgs e) => OpenAuthorizationPage();

    private void OnConfirmClick(object sender, RoutedEventArgs e) => ResolveConfirm(true);

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        AppendLog("已请求退出……");
        _cancellation.Cancel();
    }

    private void OnAccountConfirmClick(object sender, RoutedEventArgs e)
    {
        ResolveAccount(AccountList.SelectedItem as WechatAccount is { } account ? account.Account : null);
    }

    private void OnAccountSkipClick(object sender, RoutedEventArgs e) => ResolveAccount(null);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        // 用户在启动过程中关窗 = 取消启动。这里只发取消信号，不阻止关闭：
        // 编排层会在最近的取消检查点返回，随后由 App 完成子服务清理。
        _cancellation.Cancel();
    }

    private void ResolveConfirm(bool result)
    {
        ConfirmButton.Visibility = Visibility.Collapsed;
        _confirmRegistration.Dispose();
        _confirmSource?.TrySetResult(result);
        _confirmSource = null;
    }

    private void ResolveAccount(string? account)
    {
        AccountPanel.Visibility = Visibility.Collapsed;
        _accountRegistration.Dispose();
        _accountSource?.TrySetResult(account);
        _accountSource = null;
    }

    private void OpenAuthorizationPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.Instance.WechatUrl) { UseShellExecute = true });
            AppendLog($"已打开授权页面：{AppPaths.Instance.WechatUrl}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppendLog($"无法打开浏览器，请手动访问：{AppPaths.Instance.WechatUrl}");
        }
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
