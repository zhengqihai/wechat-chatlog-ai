using System.Windows;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views.Modules;

/// <summary>
/// 模块共用的弹窗入口（T16）。
///
/// 为什么需要这一层：同一个「模型设置」弹窗有两个触发点 ——
/// 用户在设置模块里点按钮，以及 VM 在 <c>InitializeAsync</c> 末尾发现"没有生效供应商"时
/// 主动抛事件做首次启动引导。开窗方式只该有一份实现，而它又必须拿到窗口当 Owner，
/// 于是让两侧各自把 owner 传进来，实现放在这里。
///
/// 只做两件事：开窗、收尾（自动回复保存成功后回读配置）。
/// 事件回调里的延后调度（<c>Dispatcher.BeginInvoke</c>）刻意留在 MainWindow ——
/// 那是"VM 事件回调栈里不能直接 ShowDialog"的窗口级约束，不属于弹窗本身。
/// </summary>
internal static class ModuleDialogs
{
    /// <summary>打开「模型与 API Key 设置」弹窗。</summary>
    public static void ShowModelSettings(Window owner, MainViewModel viewModel)
    {
        // 每次打开都新建一份弹窗 VM：表单里全是"未提交的编辑中状态"，
        // 复用同一个实例会出现"上次取消前改了一半的内容又冒出来"。
        // 关闭时由 ModelSettingsWindow 负责 Dispose()，连带取消在途的验证请求。
        var window = new ModelSettingsWindow(viewModel.CreateModelSettingsViewModel())
        {
            Owner = owner,
        };
        window.ShowDialog();
    }

    /// <summary>打开「自动回复设置」弹窗；只有保存成功才回读配置。</summary>
    public static void ShowAutoReply(Window owner, MainViewModel viewModel)
    {
        // 每次打开都新建一份弹窗 VM，理由同上。
        // 关闭时由 AutoReplyWindow 负责 CancelPending()，不留悬挂的请求。
        var window = new AutoReplyWindow(viewModel.CreateAutoReplyViewModel())
        {
            Owner = owner,
        };
        window.ShowDialog();

        // 取消或直接关窗时，主界面缓存里那份配置本来就是对的，不必回读。
        if (window.Saved)
        {
            _ = viewModel.ReloadAutoReplyConfigAsync();
        }
    }

    /// <summary>账号窗口是否已经开着（防重入）。</summary>
    private static bool _accountWindowOpen;

    /// <summary>
    /// 打开「微信账号」弹窗（T19-B）。
    ///
    /// 两个入口（rail 顶部头像、设置模块「管理」）都必须走这里：
    /// 它们各自的上下文不同，但"开窗 + 收尾"只该有一份实现 —— 与 T16 建这一层的理由相同。
    ///
    /// 收尾为什么分两种：
    /// - 用户在窗口里点了「我已切换」→ 账号真的换了，主界面所有跨账号数据都失效，
    ///   必须清空重载（§7.17 决策④）；
    /// - 其他情况（切换了但没确认 / 只是看了看）→ 只回刷账号状态：头像与设置那一行要立即反映
    ///   新的 pending 警示，而不是等下一次轮询。
    /// </summary>
    public static void ShowAccount(Window owner, MainViewModel viewModel)
    {
        // 防重入：连续点头像不该开出第二个窗口（第二个会盖住第一个，且两个都持有同一份后端状态）。
        // 标志放在这一层而不是各入口：这样两个入口天然共享同一份判据。
        if (_accountWindowOpen)
        {
            return;
        }

        _accountWindowOpen = true;
        try
        {
            var window = new AccountWindow(viewModel.CreateAccountWindowViewModel())
            {
                Owner = owner,
            };
            window.ShowDialog();

            if (window.Confirmed)
            {
                _ = viewModel.ReloadAfterAccountSwitchAsync();
            }
            else
            {
                _ = viewModel.RefreshAccountStateAsync();
            }
        }
        finally
        {
            _accountWindowOpen = false;
        }
    }
}
