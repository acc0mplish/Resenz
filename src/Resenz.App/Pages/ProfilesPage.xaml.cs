using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Resenz.App.ViewModels;

namespace Resenz.App.Pages;

public sealed partial class ProfilesPage : Page
{
    public ProfilesViewModel ViewModel { get; }

    public ProfilesPage()
    {
        ViewModel = new ProfilesViewModel(
            global::Resenz.App.AppHost.Host,
            DispatcherQueue);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        EditorTabs.SelectedItem = EditorTabs.Items.Count > 0 ? EditorTabs.Items[0] : null;
        _ = ViewModel.OpenEditorCommand.ExecuteAsync(row);
    }

    /// <summary>카드 클릭으로 해당 구독 활성화. 이미 활성이거나 버튼 위 클릭 시 중복 발동하지 않음.</summary>
    private void Card_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        if (row.IsActive) return;
        // 카드 안 버튼/입력 컨트롤 위 클릭 시 활성화하지 않음.
        if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
        _ = ViewModel.ActivateCommand.ExecuteAsync(row);
    }

    private static bool IsWithinInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                or Microsoft.UI.Xaml.Controls.TextBox
                or Microsoft.UI.Xaml.Controls.ComboBox
                or Microsoft.UI.Xaml.Controls.ToggleSwitch)
                return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.RefreshCommand.ExecuteAsync(row);
    }

    private void ToggleTime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        row.ToggleTime();
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.ActivateCommand.ExecuteAsync(row);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.RenameCommand.ExecuteAsync(row);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.DeleteCommand.ExecuteAsync(row);
    }

    private void EditInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ShowInfoEditorAsync(row);
    }

    private void EditorTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.EditorTabIndex = sender.Items.IndexOf(sender.SelectedItem);
    }

    private void SaveEditor_Click(object sender, RoutedEventArgs e)
    {
        // x:Bind 의 TextBox.Text는 기본적으로 포커스 이탈 시에만 되돌아오므로 버튼 클릭 시 동기화가 보장되지 않음. 명시적으로 한 번 더 가져옴.
        ViewModel.EditorText = RawEditor.Text;
        _ = ViewModel.SaveEditorCommand.ExecuteAsync(null);
    }

    private bool _editorMaximized;

    private void ToggleEditorMaximize_Click(object sender, RoutedEventArgs e)
    {
        _editorMaximized = !_editorMaximized;
        if (EditorPanel is not null)
            EditorPanel.Margin = _editorMaximized ? new Thickness(0) : new Thickness(40);
        EditorMaximizeIcon.Glyph = _editorMaximized ? "\uE73F" : "\uE740";
    }

    /// <summary>구독 정보 편집: 이름, 구독 주소 등.</summary>
    private async Task ShowInfoEditorAsync(ProfileRowViewModel row)
    {
        var host = global::Resenz.App.AppHost.Host;
        var item = (await host.Profiles.ListAsync()).FirstOrDefault(i => i.Id == row.Id);
        if (item is null) return;

        var nameBox = new TextBox { Header = "이름", Text = row.Name };
        var urlBox = new TextBox { Header = "구독 주소", Text = item.Source ?? "" };
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);

        var dialog = new ContentDialog
        {
            Title = "구독 정보 편집",
            Content = panel,
            PrimaryButtonText = "저장",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text?.Trim();
        var url = urlBox.Text?.Trim();

        if (!string.IsNullOrEmpty(name) && name != row.Name)
            await host.Profiles.RenameAsync(row.Id, name);

        // 구독 주소 변경: 새 주소로 다시 가져와 기존 항목을 덮어씀.
        if (!string.IsNullOrEmpty(url) && !string.Equals(url, item.Source, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var imported = await host.Profiles.ImportFromUrlAsync(url, name);
                if (row.IsActive)
                {
                    await host.Profiles.SetActiveAsync(imported.Id);
                    await host.ApplyActiveProfileAsync();
                }
                await host.Profiles.DeleteAsync(row.Id);
                ViewModel.StatusText = "구독 주소 갱신됨";
            }
            catch (Exception ex)
            {
                ViewModel.StatusText = $"구독 주소 갱신 실패: {{ex.Message}";
                return;
            }
        }
        else
        {
            ViewModel.StatusText = "구독 정보 저장됨";
        }

        await ViewModel.LoadAsync();
    }
}