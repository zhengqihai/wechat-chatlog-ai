using System.IO;
using System.Windows;
using ChatlogAI.Client.Bootstrap;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「关于」窗口（设置模块入口）。纯告知：不读也不写任何业务状态，因此没有 ViewModel，
/// 与 <see cref="DisclaimerWindow" /> 同一姿态。
///
/// 版本号从 <c>client/VERSION</c> 读取而不是写死或读程序集：它是版本号的单一事实源，
/// 改 VERSION 即可改显示，无需重编译。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = ReadVersion();
    }

    /// <summary>
    /// 唯一入口。必须先设 <c>Owner</c> 再 <c>ShowDialog</c>：Owner 是
    /// "居中在父窗口之上"（WindowStartupLocation=CenterOwner）与"真模态"的前提。
    /// </summary>
    public static void Show(Window owner) => new AboutWindow { Owner = owner }.ShowDialog();

    /// <summary>读 VERSION 文件（失败回退「未知」，不让关于窗口因为读文件失败而打不开）。</summary>
    private static string ReadVersion()
    {
        try
        {
            var path = AppPaths.Instance.VersionFile;
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }
        catch (IOException)
        {
            // 读不到就用「未知」兜底，不抛异常打断窗口构造。
        }
        catch (UnauthorizedAccessException)
        {
        }

        return "未知";
    }
}
