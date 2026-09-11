using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views.Modules;

/// <summary>
/// 朋友圈模块（T23）。原 T16 占位就地替换，MainWindow.xaml 不再改动。
///
/// View 只做 VM 做不了、也不该做的事：
/// 1. <b>首次可见</b>时触发一次加载（MainViewModel 构造时不预取时间线）；
/// 2. <b>模块不可见时关掉大图遮罩</b>——本模块是 Visibility 切换、不销毁的（§7.10 约定），
///    不显式关掉的话，切走再切回来遮罩会自己弹出来；
/// 3. 把"格子进入可视区"翻译成取图请求（<c>Loaded</c>）：这是纯 UI 事件，VM 看不见；
/// 4. 把"滚到底"翻译成翻页、把回车翻译成「应用」、把 Esc 翻译成关遮罩；
/// 5. 打开导出弹窗（需要窗口当 Owner）。
///
/// 本控件不设置 DataContext，绑定与命令都继承 MainWindow 的 MainViewModel。
/// </summary>
public partial class MomentsModuleView : UserControl
{
    public MomentsModuleView()
    {
        InitializeComponent();

        // 这里**只**监听 IsVisibleChanged、不监听 Loaded：Loaded 与可见性无关，
        // 挂在它上面等于应用一启动就把时间线拉下来，与"进模块才加载"正好相反。
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private MomentsModuleViewModel? MomentsViewModel => (DataContext as MainViewModel)?.Moments;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (MomentsViewModel is not { } moments)
        {
            return;
        }

        if (e.NewValue is true)
        {
            _ = moments.EnsureLoadedAsync();
            return;
        }

        // 切走模块：把遮罩关掉。三种关闭方式（点空白处 / Esc / 切模块）走同一个入口，
        // 状态复位只写一份在 VM 里。
        moments.CloseViewer();
    }

    /// <summary>点大图遮罩的空白处关闭（点图片本身不关，见 XAML 里的 OnViewerImageClick）。</summary>
    private void OnViewerOverlayClick(object sender, MouseButtonEventArgs e) => MomentsViewModel?.CloseViewer();

    /// <summary>把点图片的事件吃掉，避免它冒泡到遮罩那一层变成"点空白处关闭"。</summary>
    private void OnViewerImageClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>Esc 关遮罩。焦点此时一定在本模块内（开遮罩的那次点击就在模块里），所以这个隧道事件接得到。</summary>
    private void OnModuleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || MomentsViewModel is not { IsViewerOpen: true } moments)
        {
            return;
        }

        e.Handled = true;
        moments.CloseViewer();
    }

    /// <summary>
    /// 格子的 UI 元素进入可视区 = 该取图了。
    ///
    /// 这是取图的唯一触发点：条目 VM 是整批创建的，若在建 VM 时就发请求，
    /// 一页 20 条 × 最多 9 张会同时涌向后端（并发上限在 MomentMediaLoader 里）。
    /// 幂等由格子自己的状态机保证（只有"未加载"才排队）。
    /// </summary>
    private void OnMediaCellLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MomentMediaViewModel cell })
        {
            cell.RequestLoad();
        }
    }

    /// <summary>搜索框回车 = 点「应用」：搜索是服务端过滤，不能每敲一个字符请求一次。</summary>
    private void OnKeywordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || MomentsViewModel is not { } moments)
        {
            return;
        }

        e.Handled = true;

        if (moments.ApplyCommand.CanExecute(null))
        {
            moments.ApplyCommand.Execute(null);
        }
    }

    /// <summary>
    /// 滚动到底自动加载下一页。
    ///
    /// 提前一屏（约 240px）就开始取：等真滚到底再发请求，用户会看到明显的停顿。
    /// 是否该取由 VM 判断（HasMore / 是否正在加载 / 是否有基准 offset），
    /// View 只回答"现在到底了没有"这一件事。
    /// </summary>
    private void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || MomentsViewModel is not { } moments)
        {
            return;
        }

        // 内容不满一屏时没有"下一页"可言，首次加载也轮不到滚动来触发。
        if (viewer.ExtentHeight <= viewer.ViewportHeight)
        {
            return;
        }

        if (viewer.VerticalOffset + viewer.ViewportHeight < viewer.ExtentHeight - 240)
        {
            return;
        }

        _ = moments.LoadNextPageAsync();
    }

    /// <summary>
    /// 打开导出弹窗。导出永远需要一个"导什么"的上下文（这里 = 当前已应用的按人筛选），
    /// 而且导出任务跑在微信数据源侧，所以是一个独立的模态窗口而不是一次请求。
    /// </summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (MomentsViewModel is not { } moments)
        {
            return;
        }

        var window = new MomentsExportWindow(moments.CreateExportViewModel())
        {
            Owner = Window.GetWindow(this),
        };

        window.ShowDialog();
    }
}
