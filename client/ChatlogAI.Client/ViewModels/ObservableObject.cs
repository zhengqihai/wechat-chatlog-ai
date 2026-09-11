using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 视图模型基类：只做一件事——值变化时发出 <see cref="INotifyPropertyChanged"/>。
///
/// 为什么不引 CommunityToolkit.Mvvm 之类的第三方 MVVM 库：
/// 本项目的构建环境受沙箱限制（见 docs/DOTNET_CLIENT_PLAN.md §13.1），
/// 每新增一个 NuGet 依赖都要先在受限环境里恢复包，风险与收益不成比例；
/// 这里需要的能力（属性通知 + 命令）总共不到 100 行，手写反而更可控。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>仅在值确实变化时通知，并返回是否发生了变化。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
