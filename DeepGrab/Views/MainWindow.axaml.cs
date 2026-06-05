using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeepGrab.Models;
using DeepGrab.ViewModels;

namespace DeepGrab.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Explore.ScrollToEndRequested += () =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ExploreScrollViewer?.ScrollToEnd());

                // 动态绑定分类按钮
                var cats = vm.Explore.Categories;
                var btns = new[] { BtnCat0, BtnCat1, BtnCat2, BtnCat3, BtnCat4, BtnCat5, BtnCat6 };
                for (int i = 0; i < cats.Count && i < btns.Length; i++)
                    btns[i].Content = cats[i].Label;

                // 时长筛选
                foreach (var f in vm.Explore.DurationFilters)
                    DurCombo.Items.Add(new ComboBoxItem { Content = f.Label });

                await vm.InitializeAsync();
                await vm.Explore.InitAsync();
            }
        };
        KeyDown += OnKeyDown;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm &&
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox tb && tb.Name == "UrlInput")
            vm.AddDownloadCommand.Execute(null);
    }

    void OnRemoveDownload(object? s, RoutedEventArgs e)
    {
        if (s is Button b && b.Tag is DownloadTask t && DataContext is MainViewModel vm)
            vm.RemoveDownloadCommand.Execute(t);
    }

    void OnExploreSelectAll(object? s, RoutedEventArgs e)
    {
        if (s is CheckBox cb && DataContext is MainViewModel vm)
            foreach (var v in vm.Explore.Videos) v.IsSelected = cb.IsChecked == true;
    }

    void OnDownloadDoubleClick(object? s, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 && s is Border b && b.DataContext is DownloadTask t && DataContext is MainViewModel vm)
            vm.PlayFileCommand.Execute(t);
    }

    void OnCategoryClick(object? s, RoutedEventArgs e)
    {
        if (s is Button b && int.TryParse(b.Tag?.ToString(), out int idx) && DataContext is MainViewModel vm)
            vm.Explore.LoadCategoryByIndexCommand.Execute(idx);
    }
}
