using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views.Modules;

/// <summary>
/// 聊天模块（T16）。原主界面第 1/2/3 栏（会话 / 消息 + 筛选 / AI 分析）整体搬进这里，行为不变。
///
/// 本控件只承担模块自己的两件事：
/// 1. 订阅 VM 的"列表已更新"事件并滚动到底 —— 它操作的是本控件里的 ChatList / MessageList /
///    AiThreadScroll，这三个名字也随代码搬进了本控件的命名域（UserControl 自带命名域，
///    留在 MainWindow.xaml.cs 里引用会直接编译不过，这正是重构想要的"编译器帮我找出漏搬的地方"）；
/// 2. 打开导出弹窗 —— 导出要的是"当前选中会话 + 当前筛选"这份模块内上下文。
///
/// 弹窗的 Owner 用 <see cref="Window.GetWindow"/> 现取，而不是反过来引用 MainWindow 类型：
/// 子模块反向依赖宿主窗口，以后这个模块要嵌到别的窗口里就直接编不过。
///
/// 本控件不设置 DataContext，全部绑定（含 RelativeSource=Window 的那些）都依赖
/// MainWindow 把 MainViewModel 从可视树上继承下来。
/// </summary>
public partial class ChatModuleView : UserControl
{
    private MainViewModel? _viewModel;

    public ChatModuleView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 在 Loaded 里订阅而不是构造器里：构造器跑在 MainWindow.InitializeComponent() 中间，
    /// 那时窗口的 DataContext 还没赋值，拿不到 VM。Loaded 时 DataContext 已沿继承链就位。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.MessagesUpdated += OnMessagesUpdated;
        viewModel.AiThreadUpdated += OnAiThreadUpdated;
    }

    /// <summary>
    /// 退订。VM 比窗口活得久（App 在退出路径里才 Dispose 它），窗口关闭后若还挂着订阅，
    /// 一次轮询续体就会把事件打到已经卸载的控件上（docs/DOTNET_CLIENT_PLAN.md §7.5 记过这个坑）。
    ///
    /// 本控件在切模块时只是 Visibility 变化、不会被移出可视树，所以 Unloaded 只在真正
    /// 离开可视树（关窗）时发生，"切模块"不会误触发退订。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.MessagesUpdated -= OnMessagesUpdated;
        _viewModel.AiThreadUpdated -= OnAiThreadUpdated;
        _viewModel = null;
    }

    private void OnMessagesUpdated()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        // ItemsSource 刚被整体替换时，容器还没生成，此刻直接 ScrollIntoView 会落空。
        // 排到 Background 优先级之后执行，等这一轮布局跑完再滚到底。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (viewModel.Messages.Count == 0)
            {
                return;
            }

            MessageList.ScrollIntoView(viewModel.Messages[^1]);
        }));
    }

    /// <summary>AI 对话是 ScrollViewer + ItemsControl（见 XAML 注释），所以直接滚到底即可。</summary>
    private void OnAiThreadUpdated() => AiThreadScroll.ScrollToEnd();

    /// <summary>
    /// 导出当前会话（原 MainWindow.OnExportClick，逻辑一字未改）。
    /// 放在本模块的工具栏而不是图标栏：导出永远需要一个"导什么"的上下文，
    /// 脱离上下文的导出入口还得再问一次"导哪个"（见 docs/DOTNET_CLIENT_PLAN.md §14.2.2）。
    /// </summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedChat is not { } chat)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "请先在左侧选择一个会话，再导出。",
                "Chatlog AI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // 只取"点下按钮这一刻"的上下文快照：导出期间主界面仍在 5 秒轮询，
        // 用户完全可能切走会话或改筛选，那样就会出现"对话框里写着 A 会话、导出的却是 B"。
        var context = new ExportContext
        {
            ChatId = chat.Id,
            ChatName = chat.Name,
            ChatTypeText = chat.TypeText,
            HasTimeLimit = viewModel.RecentHours is not null,
            TimeUnit = viewModel.TimeUnit,
            TimeValue = viewModel.TimeValue,
        };

        // 「条数」筛选刻意不传：按规格导出的是"时间范围内的全部消息（上限 2000 条）"，
        // 主界面"最近 N 条"只是显示窗口，不该变成导出的隐含限制。
        var window = new ExportWindow(new ExportViewModel(context)) { Owner = Window.GetWindow(this) };
        window.ShowDialog();

        if (window.ResultMessage is { Length: > 0 } message)
        {
            MessageBox.Show(Window.GetWindow(this), message, "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
