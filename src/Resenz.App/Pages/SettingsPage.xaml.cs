using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Resenz.App.ViewModels;

namespace Resenz.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private bool _syncing;
    private bool _attached;

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(global::Resenz.App.AppHost.Host);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Attach();
        await ReloadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Detach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        // 다른 화면(제목 표시줄, 미니 패널)에서 토글이나 코어 상태를 바꿀 때 설정 페이지가 동기화 새로고침.
        AppSignals.SwitchesChanged += OnExternalChange;
        AppHost.Host.Core.StateChanged += OnCoreStateChanged;
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        AppSignals.SwitchesChanged -= OnExternalChange;
        AppHost.Host.Core.StateChanged -= OnCoreStateChanged;
    }

    // 이벤트는 백그라운드 스레드에서 발생할 수 있음: 반드시 UI 스레드로 디스패치한 뒤 Reload,
    // 그렇지 않으면 LoadAsync의 속성 대입이 x:Bind의 경계 넘는 컨트롤 갱신을 유발해 COMException 발생.
    private void OnExternalChange(object? sender, EventArgs e) => DispatchReload();

    private void OnCoreStateChanged(object? sender, Resenz.Core.Services.CoreStateChanged e) => DispatchReload();

    private void DispatchReload()
    {
        if (DispatcherQueue.HasThreadAccess)
            _ = ReloadAsync();
        else
            DispatcherQueue.TryEnqueue(() => _ = ReloadAsync());
    }

    private async Task ReloadAsync()
    {
        await ViewModel.LoadAsync();

        _syncing = true;
        try
        {
            ThemeBox.SelectedIndex = ViewModel.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
            LogLevelBox.SelectedIndex = ViewModel.LogLevel switch
            {
                "silent" => 0,
                "error" => 1,
                "warning" => 2,
                "debug" => 4,
                _ => 3,
            };
            SystemProxyModeBox.SelectedIndex = ViewModel.SystemProxyMode switch
            {
                "pac" => 1,
                _ => 0,
            };
            TunStackBox.SelectedIndex = ViewModel.TunStack switch
            {
                "gvisor" => 1,
                "system" => 2,
                _ => 0,
            };
            var proxyIndex = ViewModel.GithubProxyIndex;
            GithubProxyBox.SelectedIndex = proxyIndex >= 0 && proxyIndex < SettingsViewModel.GithubProxyBuiltins.Length
                ? proxyIndex
                : SettingsViewModel.GithubProxyBuiltins.Length;
            GithubProxyCustomBox.Visibility = GithubProxyBox.SelectedIndex == SettingsViewModel.GithubProxyBuiltins.Length
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _syncing = false;
        }

        // 코어 버전 목록은 네트워크 사용(느릴 수 있음)하므로 백그라운드 로드로 위 드롭다운 초기화를 막지 않음.
        if (!ViewModel.LoadKernelVersionsCommand.IsRunning)
            _ = ViewModel.LoadKernelVersionsCommand.ExecuteAsync(null);
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.Theme = ThemeBox.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "default",
        };
    }

    private void LogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.LogLevel = LogLevelBox.SelectedIndex switch
        {
            0 => "silent",
            1 => "error",
            2 => "warning",
            4 => "debug",
            _ => "info",
        };
    }

    private void SystemProxyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.SystemProxyMode = SystemProxyModeBox.SelectedIndex switch
        {
            1 => "pac",
            _ => "manual",
        };
    }

    private void TunStack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.TunStack = TunStackBox.SelectedIndex switch
        {
            1 => "gvisor",
            2 => "system",
            _ => "mixed",
        };
    }

    private void GithubProxy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.GithubProxyIndex = GithubProxyBox.SelectedIndex;
        GithubProxyCustomBox.Visibility = GithubProxyBox.SelectedIndex == SettingsViewModel.GithubProxyBuiltins.Length
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>텍스트 상자 포커스 이탈 시 제출.</summary>
    private void TextField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.Tag is string field) CommitTextField(field, box.Text);
    }

    /// <summary>Enter로 제출하고 포커스를 입력 상자 밖으로 옮김(포커스 이탈 트리거로 명확한 저장 피드백 제공).</summary>
    private void TextField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (sender is not TextBox box) return;
        if (box.Tag is string field) CommitTextField(field, box.Text);
        e.Handled = true;
        // 포커스를 루트 컨테이너로 옮겨 편집을 종료(WinUI Desktop은 SearchRoot 없는 TryMoveFocus 사용 불가).
        RootGrid.Focus(FocusState.Programmatic);
    }

    /// <summary>페이지 빈 곳 클릭 시 입력 상자 편집 종료.</summary>
    private void Root_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox) return;
        // 클릭 원점이 입력 상자 안이면 그 포커스를 유지하고 옮기지 않음.
        if (IsWithinTextBox(e.OriginalSource as DependencyObject)) return;
        RootGrid.Focus(FocusState.Programmatic);
    }

    private static bool IsWithinTextBox(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBox) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>단축키 입력: 읽기 전용 입력 상자에서 조합키를 누르면 기록. Backspace로 비움.</summary>
    private async void Hotkey_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string action) return;
        e.Handled = true;

        // 수정키 단독 입력 무시(기본키 대기).
        if (e.Key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Menu
            or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftWindows
            or Windows.System.VirtualKey.RightWindows)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Back)
        {
            await ClearHotkeyAsync(action, box);
            return;
        }

        var mods = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var win = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows);

        var modifiers = new System.Text.StringBuilder();
        if ((mods & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Ctrl+");
        if ((alt & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Alt+");
        if ((shift & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Shift+");
        if ((win & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Win+");

        var text = modifiers + KeyName(e.Key);
        if (!Resenz.App.Services.HotkeyParser.TryParse(text, out _, out _))
        {
            ViewModel.StatusText = "단축키 무효: 수정키 최소 하나 필요 + 사용 가능한 기본키";
            return;
        }

        await ApplyHotkeyAsync(action, text, box);
    }

    /// <summary>단축키 바인딩. 앱 내 다른 동작과 충돌하면 대화상자로 선점 여부 확인.</summary>
    private async Task ApplyHotkeyAsync(string action, string text, TextBox? box)
    {
        var result = await ViewModel.TryBindHotkeyAsync(action, text);
        switch (result.Status)
        {
            case Resenz.App.Services.HotkeyApplyStatus.Ok:
                if (box is not null) box.Text = text;
                break;

            case Resenz.App.Services.HotkeyApplyStatus.InternalConflict when result.Conflict is not null:
                var conflictName = SettingsViewModel.HotkeyDisplayName(result.Conflict.Value);
                var dialog = new ContentDialog
                {
                    Title = "단축키 충돌",
                    Content = $"'{text}' 키는 이미 '{conflictName}'에 바인딩되어 있습니다.\n상대의 바인딩을 제거하고 여기로 다시 바인딩할까요?",
                    PrimaryButtonText = "다시 바인딩",
                    CloseButtonText = "취소",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    var ok = await ViewModel.ResolveHotkeyConflictAsync(action, text, result.Conflict.Value.ToString());
                    if (ok && box is not null) box.Text = text;
                }
                else
                {
                    // 취소: 입력 상자를 현재 동작의 실제 바인딩으로 복원.
                    if (box is not null) box.Text = await GetCurrentHotkeyTextAsync(action);
                }
                break;

            case Resenz.App.Services.HotkeyApplyStatus.Occupied:
                ViewModel.StatusText = "단축키 등록 실패: 다른 프로그램이 점유 중일 수 있음";
                if (box is not null) box.Text = await GetCurrentHotkeyTextAsync(action);
                break;

            case Resenz.App.Services.HotkeyApplyStatus.Invalid:
                ViewModel.StatusText = "단축키 무효: 수정키 최소 하나 필요 + 사용 가능한 기본키";
                if (box is not null) box.Text = await GetCurrentHotkeyTextAsync(action);
                break;
        }
    }

    private async Task<string> GetCurrentHotkeyTextAsync(string action)
    {
        var host = global::Resenz.App.AppHost.Host;
        return await host.Settings.GetAsync($"hotkey.{action}") ?? "";
    }

    private async Task<bool> ClearHotkeyAsync(string action, TextBox box)
    {
        var result = await ViewModel.TryBindHotkeyAsync(action, "");
        if (result.Status is Resenz.App.Services.HotkeyApplyStatus.Cleared)
        {
            box.Text = "";
            return true;
        }
        return false;
    }

    /// <summary>VirtualKey를 해석기가 아는 이름으로 변환.</summary>
    private static string KeyName(Windows.System.VirtualKey key)
    {
        var v = (int)key;
        if (v is >= 0x41 and <= 0x5A) return ((char)v).ToString();   // A-Z
        if (v is >= 0x30 and <= 0x39) return ((char)v).ToString();   // 0-9
        if (v is >= 0x70 and <= 0x87) return $"F{v - 0x70 + 1}";      // F1-F24
        if (v is >= 0x60 and <= 0x69) return $"NumPad{v - 0x60}";     // 키패드 0-9
        return key switch
        {
            Windows.System.VirtualKey.Space => "Space",
            Windows.System.VirtualKey.Tab => "Tab",
            Windows.System.VirtualKey.Enter => "Enter",
            Windows.System.VirtualKey.Escape => "Esc",
            Windows.System.VirtualKey.Back => "Backspace",
            Windows.System.VirtualKey.Delete => "Delete",
            Windows.System.VirtualKey.Insert => "Insert",
            Windows.System.VirtualKey.Home => "Home",
            Windows.System.VirtualKey.End => "End",
            Windows.System.VirtualKey.PageUp => "PageUp",
            Windows.System.VirtualKey.PageDown => "PageDown",
            Windows.System.VirtualKey.Up => "Up",
            Windows.System.VirtualKey.Down => "Down",
            Windows.System.VirtualKey.Left => "Left",
            Windows.System.VirtualKey.Right => "Right",
            Windows.System.VirtualKey.NumberKeyLock => "NumLock",
            _ => "",
        };
    }

    private void CommitTextField(string field, string text)
    {
        switch (field)
        {
            case "MixedPort": ViewModel.MixedPort = text; break;
            case "ControllerPort": ViewModel.ControllerPort = text; break;
            case "DelayTestUrl": ViewModel.DelayTestUrl = text; break;
            case "DelayTestTimeout": ViewModel.DelayTestTimeout = text; break;
            case "DelayTestConcurrency": ViewModel.DelayTestConcurrency = text; break;
            case "SubscriptionUserAgent": ViewModel.SubscriptionUserAgent = text; break;
            case "SubscriptionTimeout": ViewModel.SubscriptionTimeout = text; break;
            case "GithubProxyCustom": ViewModel.GithubProxyCustom = text; break;
            case "AutoQuitWithoutCoreDelay": ViewModel.AutoQuitWithoutCoreDelay = int.TryParse(text, out var d) ? d : ViewModel.AutoQuitWithoutCoreDelay; break;
            case "TunMtu": ViewModel.TunMtu = text; break;
            case "TunDnsHijack": ViewModel.TunDnsHijack = text; break;
            case "TunRouteExcludeAddress": ViewModel.TunRouteExcludeAddress = text; break;
            case "PauseSsids": ViewModel.PauseSsids = text; break;
            case "SsidProfileMap": ViewModel.SsidProfileMap = text; break;
        }
    }
}
