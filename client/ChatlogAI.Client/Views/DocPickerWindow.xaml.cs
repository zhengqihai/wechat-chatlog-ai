using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 知识文档多选器（T-F4）。纯选择 + 大小约束，无业务状态，因此不建 ViewModel，
/// 直接用 code-behind 维护勾选与累计大小（与 <see cref="LogViewerWindow" /> 同一姿态）。
/// </summary>
public partial class DocPickerWindow : Window
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly ObservableCollection<DocPickerItem> _items = [];

    public DocPickerWindow(IReadOnlyList<DocItem> docs, IReadOnlyList<string>? selectedIds)
    {
        InitializeComponent();

        var selected = new HashSet<string>(selectedIds ?? [], StringComparer.Ordinal);
        foreach (var doc in docs)
        {
            var item = new DocPickerItem(doc, selected.Contains(doc.Id));
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }

        DocList.ItemsSource = _items;
        UpdateSummary();
    }

    /// <summary>用户选中的文档 id（按文档顺序）。</summary>
    public IReadOnlyList<string> SelectedDocIds { get; private set; } = [];

    /// <summary>
    /// 打开选择器。返回选中 id；用户取消返回 <c>null</c>（调用方据此保持原值不变）。
    /// </summary>
    public static IReadOnlyList<string>? Pick(
        Window owner,
        IReadOnlyList<DocItem> docs,
        IReadOnlyList<string>? selectedIds)
    {
        var window = new DocPickerWindow(docs, selectedIds) { Owner = owner };
        return window.ShowDialog() == true ? window.SelectedDocIds : null;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocPickerItem.IsSelected))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        var total = _items.Where(item => item.IsSelected).Sum(item => item.Size);
        var remain = MaxBytes - total;

        SummaryText.Text = remain < 0
            ? $"已选 {FormatSize(total)}，超出 4MB 上限，请取消部分文档"
            : $"已选 {FormatSize(total)} / 4MB，剩余 {FormatSize(remain)}";

        ConfirmButton.IsEnabled = remain >= 0;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        SelectedDocIds = _items.Where(item => item.IsSelected).Select(item => item.Id).ToList();
        DialogResult = true;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }
}

/// <summary>多选器里的一行：文档 + 勾选态。</summary>
public sealed class DocPickerItem : ObservableObject
{
    public DocPickerItem(DocItem model, bool selected)
    {
        Model = model;
        _isSelected = selected;
    }

    public DocItem Model { get; }

    public string Id => Model.Id;

    public string Filename => Model.Filename;

    public long Size => Model.Size;

    public string SizeText => Size < 1024
        ? $"{Size} B"
        : Size < 1024 * 1024
            ? $"{Size / 1024.0:0.#} KB"
            : $"{Size / 1024.0 / 1024.0:0.##} MB";

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
