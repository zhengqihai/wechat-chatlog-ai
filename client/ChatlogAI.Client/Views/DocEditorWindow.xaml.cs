using System.IO;
using System.Text;
using System.Windows;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 知识文档编辑器（T-F4）。只负责产出「文件名 + 正文」两个值，不自己落库：
/// 同名覆盖确认与上传都交由 <see cref="Modules.DocsModuleView"/> 复用同一套逻辑，
/// 这样「上传文件」与「在 app 内编写」两条路径不会各写一份校验。
/// </summary>
public partial class DocEditorWindow : Window
{
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "txt", "html", "json",
    };

    public DocEditorWindow(string initialFilename = "", string initialContent = "")
    {
        InitializeComponent();
        FilenameBox.Text = initialFilename;
        ContentBox.Text = initialContent;
    }

    public string Filename => FilenameBox.Text.Trim();

    /// <summary>命名为 DocumentContent 以避免遮蔽 <see cref="ContentControl.Content"/>。</summary>
    public string DocumentContent => ContentBox.Text;

    /// <summary>
    /// 打开编辑器。保存返回 (filename, content)；取消返回 <c>null</c>。
    /// </summary>
    public static (string Filename, string Content)? Create(Window? owner)
    {
        var window = new DocEditorWindow { Owner = owner };
        return window.ShowDialog() == true ? (window.Filename, window.DocumentContent) : null;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = Filename;
        if (name.Length == 0)
        {
            MessageBox.Show(this, "请填写文件名。", "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            MessageBox.Show(this, "仅支持 md / txt / html / json 格式。", "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Encoding.UTF8.GetByteCount(DocumentContent) > MaxFileBytes)
        {
            MessageBox.Show(this, "文档超过 4MB 上限。", "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
