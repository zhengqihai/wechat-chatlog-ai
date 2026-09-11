using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「微信账号」窗口（T19-B，规格见 docs/DOTNET_CLIENT_PLAN.md §7.17）。
///
/// View 只做四件 VM 做不了的事（都在 MVVM 的硬边界上）：
/// 1. 把"需要提示 / 需要二次确认"翻译成 <c>MessageBox</c>；
/// 2. 用系统默认浏览器打开授权页（VM 不引用进程与 Win32）；
/// 3. 把"该关窗了"翻译成 <c>Close()</c>；
/// 4. 关窗时释放 VM（连带停掉授权轮询与在途请求）。
///
/// 全程**不带 WebView2**：授权页是上游的浏览器界面，用系统浏览器打开是本项目的既定决策
/// （§7.17 决策③），把浏览器内核塞进客户端只会多一个依赖与一条升级路径。
/// </summary>
public partial class AccountWindow : Window
{
    private readonly AccountWindowViewModel _viewModel;

    public AccountWindow(AccountWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.AlertRequested += OnAlertRequested;
        _viewModel.ConfirmRequested += OnConfirmRequested;
        _viewModel.AuthorizeRequested += OnAuthorizeRequested;
        _viewModel.CloseRequested += OnCloseRequested;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// 用户是否完成了「我已切换」。调用方据此决定要不要清空重载。
    ///
    /// 与 <see cref="AutoReplyWindow.Saved"/> 同一理由：本窗口的关闭原因有三种
    /// （确认成功 / 只想看一眼 / 点右上角关闭），<c>DialogResult</c> 只有两态表达不了。
    /// </summary>
    public bool Confirmed => _viewModel.Confirmed;

    /// <summary>
    /// 不 await：让窗口先显示出来，账号清单在后台拉（列表本来就有"正在读取"的空态）。
    /// <see cref="AccountWindowViewModel.InitializeAsync"/> 契约是"不抛异常"
    /// （失败转成占位文案），所以这里可以安全丢弃这个 Task。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => _ = _viewModel.InitializeAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnAlertRequested(string message)
        => MessageBox.Show(this, message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// 切换账号的二次确认。用 <see cref="MessageBoxImage.Warning"/> 而不是 Question：
    /// 这一步一旦确认就会关掉发送闸门，需要用户在视觉上停一下；
    /// 默认按钮给 Cancel，避免一路回车把账号切了。
    /// </summary>
    private bool OnConfirmRequested(string message)
        => MessageBox.Show(
               this,
               message,
               "Chatlog AI",
               MessageBoxButton.OKCancel,
               MessageBoxImage.Warning,
               MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>
    /// 用系统默认浏览器打开授权页。
    ///
    /// <c>UseShellExecute = true</c> 不能省：为 false 时 .NET 会把 URL 当可执行文件去启动，
    /// 直接抛异常（这是 .NET Core 相对 .NET Framework 的行为变化）。
    /// 打开失败只提示、不抛出——授权页打不开不该把整个窗口带崩。
    /// </summary>
    private void OnAuthorizeRequested(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(
                this, "打开浏览器失败：" + ex.Message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseRequested() => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        // VM 的生命周期跟随本窗口：窗口关了就不该再有绑定在打它，
        // 也必须停掉授权轮询，否则那条后台循环会一直打 /account/resolve-status。
        _viewModel.AlertRequested -= OnAlertRequested;
        _viewModel.ConfirmRequested -= OnConfirmRequested;
        _viewModel.AuthorizeRequested -= OnAuthorizeRequested;
        _viewModel.CloseRequested -= OnCloseRequested;
        Loaded -= OnLoaded;
        Closed -= OnClosed;

        _viewModel.Dispose();
    }
}
