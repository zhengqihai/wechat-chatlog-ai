using System.ComponentModel;
using System.Windows;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「模型与 API Key 设置」对话框（T9）。
///
/// View 只做三件 VM 做不了的事，其余（取数、预设、验证门禁、编辑态、回刷）全在
/// <see cref="ModelSettingsViewModel"/> 里，本文件不出现任何业务流程：
///
/// 1. <b>把 PasswordBox 接上 VM</b>。PasswordBox.Password 不是依赖属性，
///    没法在 XAML 里 {Binding}，也没有 UpdateSourceTrigger 可用，只能靠事件同步。
/// 2. <b>弹框与二次确认</b>（MessageBox）—— VM 不引用 MessageBox，走事件回抛。
/// 3. <b>开窗时取数与关窗时释放</b>。
///
/// 关于 API Key 的同步，有一个关键约束：VM 也会"反向"改这个值
/// （<c>ResetForm()</c> 清空表单、<c>BeginEdit()</c> 故意把 Key 置空表示"沿用旧 Key"）。
/// 如果只做 View → VM 的同步，用户点了"编辑"之后框里还留着上一次输入的明文 Key，
/// 而 VM 里已经把它当成"没填"。所以两个方向都要通。
///
/// </summary>
public partial class ModelSettingsWindow : Window
{
    private readonly ModelSettingsViewModel _viewModel;

    /// <summary>
    /// 正在由代码回写 <see cref="System.Windows.Controls.PasswordBox.Password"/>，
    /// 此时 <c>PasswordChanged</c> 是"自己绕回来"的回声，不要再推给 VM。
    /// 不加这个标志虽然也能收敛（VM 的 setter 遇到相同值会提前返回，不会无限循环），
    /// 但会白白走一遍"回写 → 事件 → setter → 比较"，而且一旦将来 VM 侧加了
    /// 归一化（如 Trim）就会真的转起来。这里显式挡掉。
    /// </summary>
    private bool _syncingPassword;

    public ModelSettingsWindow(ModelSettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.AlertRequested += OnAlertRequested;
        _viewModel.ConfirmRequested += OnConfirmRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 取数放在 Loaded 而不是构造器里：LoadProvidersAsync 是 async，
        // 构造器里没法 await（.Result / .Wait() 会锁 UI 线程），
        // 而此刻窗口还没显示，出问题也没有可见的宿主。
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync();
    }

    /// <summary>用户在这个框里打字 → 推给 VM。VM 的 setter 会顺手作废"已验证"的结论。</summary>
    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword)
        {
            return;
        }

        _viewModel.ApiKey = ApiKeyBox.Password;
    }

    /// <summary>VM 改 Key（清空表单 / 进入编辑态）→ 回写到框里。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModelSettingsViewModel.ApiKey))
        {
            return;
        }

        if (string.Equals(ApiKeyBox.Password, _viewModel.ApiKey, StringComparison.Ordinal))
        {
            return;
        }

        _syncingPassword = true;
        try
        {
            ApiKeyBox.Password = _viewModel.ApiKey;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    /// <summary>对应网页版的 <c>window.alert</c>；owner 传 this，弹框才会压在本窗口之上。</summary>
    private void OnAlertRequested(string message)
        => MessageBox.Show(this, message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>对应网页版的 <c>window.confirm</c>。默认按钮给"取消"，避免顺手回车把供应商删掉。</summary>
    private bool OnConfirmRequested(string message)
        => MessageBox.Show(this, message, "Chatlog AI", MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>
    /// 「关闭」走 <c>Close()</c> 而不是 <c>DialogResult</c>：本窗口没有"确定 / 取消"两态语义，
    /// 编辑是即时生效的（保存按钮各自落库），关窗只是离开。
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        // VM 的生命周期跟随本窗口：关窗就释放，连带取消在途请求
        // （用户点完"验证 Key"就关窗时，那条最长 180 秒的请求在这里被取消）。
        _viewModel.AlertRequested -= OnAlertRequested;
        _viewModel.ConfirmRequested -= OnConfirmRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();

        Closed -= OnClosed;
    }
}
