using System.Windows;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 启动免责声明窗口（T17）。
///
/// 它是**纯告知**：没有"同意 / 不同意"的分支，也不做"不同意就退出"的门禁——
/// 那是许可协议的做法，这里不是（§14.5.3）。
///
/// 唯一的交互结果只有一点：用户可能勾选"不再提示"，勾了写进 ClientSettings，
/// 下次启动直接跳过。所以本类对外只有两个静态入口——
/// <see cref="ShowIfNeeded" />（启动路径）与 <see cref="ShowAgain" />（设置里的回头路）。
/// 为什么不把"决定弹不弹"放在调用方：调用方（App 与设置模块）各有各的上下文，
/// 判据只该有一处，否则以后加第三个入口时一定会漏判。
/// </summary>
public partial class DisclaimerWindow : Window
{
    public DisclaimerWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    /// <summary>
    /// 启动时调用：勾过"不再提示"就直接返回。
    /// **先判、再建窗**的顺序是刻意的：先 new 出来再关掉会在屏幕上闪一帧空白对话框。
    /// </summary>
    public static void ShowIfNeeded(Window owner)
    {
        if (App.Settings.SuppressDisclaimer)
        {
            return;
        }

        ShowCore(owner);
    }

    /// <summary>
    /// 设置模块里"重新显示免责声明"的入口：无视"不再提示"强制弹一次。
    /// 不在这里预先把标记清掉——清不清由用户在窗口里的勾选决定（见 <see cref="OnClosed" />），
    /// 这样"打开看一眼又关掉"也会把标记清掉，符合"我要重新看到它"的预期。
    /// </summary>
    public static void ShowAgain(Window owner)
    {
        ShowCore(owner);
    }

    /// <summary>
    /// 必须先设 <c>Owner</c> 再 <c>ShowDialog</c>：
    /// Owner 是"居中在父窗口之上"（WindowStartupLocation=CenterOwner）与
    /// "真模态"（父窗口不响应输入）的前提；设晚了窗口会先按默认位置显示。
    /// </summary>
    private static void ShowCore(Window owner)
    {
        var window = new DisclaimerWindow { Owner = owner };
        window.ShowDialog();
    }

    /// <summary>
    /// 关闭时统一落盘。放在 Closed 而不是按钮的 Click：用户也可能按 Esc 或点右上角 X，
    /// 三条路径都要记下勾选结果，否则"勾了不再提示又用 X 关掉"会白勾。
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        App.Settings.SuppressDisclaimer = DontShowAgain.IsChecked == true;
        App.Settings.Save();
    }
}
