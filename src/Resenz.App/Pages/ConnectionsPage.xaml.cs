using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using Resenz.App.ViewModels;

namespace Resenz.App.Pages;

public sealed partial class ConnectionsPage : Page
{
    public ConnectionsViewModel ViewModel { get; }
    private bool _syncingSort;

    public ConnectionsPage()
    {
        ViewModel = new ConnectionsViewModel(
            global::Resenz.App.AppHost.Host.Core,
            DispatcherQueue);
        DataContext = ViewModel;
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        ViewModel.RenderNow();
        SyncSortBox();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void SyncSortBox()
    {
        _syncingSort = true;
        try
        {
            SortBox.SelectedIndex = ViewModel.SortBy switch
            {
                "upload" => 1,
                "download" => 2,
                "process" => 3,
                "host" => 4,
                _ => 0,
            };
            RealtimeButton.IsChecked = !ViewModel.IsPaused;
        }
        finally
        {
            _syncingSort = false;
        }
    }

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSort) return;
        ViewModel.SortBy = SortBox.SelectedIndex switch
        {
            1 => "upload",
            2 => "download",
            3 => "process",
            4 => "host",
            _ => "duration",
        };
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
        => ViewModel.SortDescending = !ViewModel.SortDescending;

    private void HeaderSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string field) return;
        ViewModel.ToggleSortCommand.Execute(field);
    }

    private void Realtime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        ViewModel.IsPaused = button.IsChecked != true;
    }

    private void ToggleView_Click(object sender, RoutedEventArgs e)
        => ViewModel.IsTableView = !ViewModel.IsTableView;

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        // 열 가시성을 메뉴 체크 상태에 동기화.
        foreach (var item in ViewModel.Columns)
        {
            var menuItem = item.Key switch
            {
                "process" => ColProcess,
                "host" => ColHost,
                "network" => ColNetwork,
                "rule" => ColRule,
                "upload" => ColUpload,
                "download" => ColDownload,
                _ => ColDuration,
            };
            menuItem.IsChecked = item.IsVisible;
        }
    }

    private void Column_Toggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item || item.Tag is not string key) return;
        var column = ViewModel.Columns.FirstOrDefault(c => c.Key == key);
        if (column is null) return;
        column.IsVisible = item.IsChecked;
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ConnectionRowViewModel row) return;
        _ = ShowDetailsAsync(row);
    }

    private async Task ShowDetailsAsync(ConnectionRowViewModel row)
    {
        var content = new StackPanel { Spacing = 8, MinWidth = 360 };
        AddDetailRow(content, "프로세스", row.Process);
        AddDetailRow(content, "호스트", row.Host);
        AddDetailRow(content, "네트워크", row.Network);
        AddDetailRow(content, "경로", row.Chain);
        AddDetailRow(content, "규칙", row.Rule);
        AddDetailRow(content, "출처", row.Source);
        AddDetailRow(content, "대상", row.Destination);
        AddDetailRow(content, "업로드", row.Upload);
        AddDetailRow(content, "다운로드", row.Download);
        AddDetailRow(content, "지속 시간", row.Duration);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"연결 상세 · {row.Host}",
            Content = content,
            PrimaryButtonText = "규칙으로 복사",
            CloseButtonText = "닫기",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            CopyRule(row);
    }

    private static void AddDetailRow(StackPanel panel, string label, string value)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        text.Inlines.Add(new Run { Text = label + ": ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Run { Text = string.IsNullOrEmpty(value) ? "—" : value });
        panel.Children.Add(text);
    }

    private void CopyRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ConnectionRowViewModel row) return;
        CopyRule(row);
    }

    private static void CopyRule(ConnectionRowViewModel row)
    {
        var text = RuleTextBuilder.Build(row);
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}

