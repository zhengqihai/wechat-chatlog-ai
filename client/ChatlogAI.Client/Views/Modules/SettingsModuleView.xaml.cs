using System.Windows;
using System.Windows.Controls;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views.Modules;

/// <summary>
/// 设置模块（T16）。本轮只把两个配置入口的点击从 MainWindow 搬过来，
/// 弹窗怎么开交给 <see cref="ModuleDialogs"/>，本类只负责"点哪个按钮"。
///
/// 本控件不设置 DataContext，绑定继承 MainWindow 的 MainViewModel。
/// </summary>
public partial class SettingsModuleView : UserControl
{
    public SettingsModuleView() => InitializeComponent();

    private void OnModelSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        ModuleDialogs.ShowModelSettings(owner, viewModel);
    }

    private void OnAutoReplyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        ModuleDialogs.ShowAutoReply(owner, viewModel);
    }

    /// <summary>
    /// 「管理」微信账号（T19-B）：与 rail 顶部头像走**同一个**弹窗入口，
    /// 开关窗与"确认切换后清空重载"都收在 <see cref="ModuleDialogs.ShowAccount"/> 里。
    /// 这里只需要 MainViewModel —— 状态与收尾都要落到它身上，不给弹窗另起一份。
    /// </summary>
    private void OnManageAccountClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        ModuleDialogs.ShowAccount(owner, viewModel);
    }

    /// <summary>
    /// 重新显示免责声明（T17）。
    ///
    /// 与前两个入口不同，这里**不需要** MainViewModel：免责声明是纯告知，
    /// 不读也不写任何业务状态，取了 VM 反而是多余的耦合。
    /// Owner 必须显式传当前窗口，弹窗才会居中在设置页之上并保持真模态。
    /// </summary>
    private void OnShowDisclaimerClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        DisclaimerWindow.ShowAgain(owner);
    }

    /// <summary>
    /// 「关于」。与免责声明一样是纯告知，不需要 MainViewModel；
    /// 入口做成 AboutWindow 自己的静态方法（判据与开窗方式只留一份），这里只传 Owner。
    /// </summary>
    private void OnShowAboutClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        AboutWindow.Show(owner);
    }
}
