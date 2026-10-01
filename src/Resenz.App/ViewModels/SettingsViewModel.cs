using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resenz.App.Services;
using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ResenzHost _host;
    private bool _loading;
    private bool _applyingSwitch;

    [ObservableProperty]
    public partial bool AutoStartCore { get; set; }

    [ObservableProperty]
    public partial bool StartupWithWindows { get; set; }

    [ObservableProperty]
    public partial bool SystemProxyEnabled { get; set; }

    [ObservableProperty]
    public partial string SystemProxyMode { get; set; } = "manual";

    [ObservableProperty]
    public partial bool TunEnabled { get; set; }

    [ObservableProperty]
    public partial string Theme { get; set; } = "default";

    [ObservableProperty]
    public partial int NavigationStyle { get; set; }

    /// <summary>배경 소재: 0=Mica,1=아크릴(Mica Alt), 2=단색.</summary>
    [ObservableProperty]
    public partial int BackdropStyle { get; set; }

    [ObservableProperty]
    public partial string MixedPort { get; set; } = "7890";

    [ObservableProperty]
    public partial string ControllerPort { get; set; } = "9090";

    [ObservableProperty]
    public partial string Mode { get; set; } = "rule";

    [ObservableProperty]
    public partial string LogLevel { get; set; } = "info";

    [ObservableProperty]
    public partial bool AllowLan { get; set; }

    [ObservableProperty]
    public partial bool Ipv6 { get; set; } = true;

    [ObservableProperty]
    public partial string CorePath { get; set; } = "";

    [ObservableProperty]
    public partial string DataDirectory { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string KernelVersion { get; set; } = "알 수 없음";

    [ObservableProperty]
    public partial string LatestKernelVersion { get; set; } = "—";

    [ObservableProperty]
    public partial bool KernelHasUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsKernelBusy { get; set; }

    [ObservableProperty]
    public partial string KernelStatusText { get; set; } = "아직 검사 전";

    [ObservableProperty]
    public partial double KernelProgress { get; set; }

    [ObservableProperty]
    public partial bool KernelProgressVisible { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<string> KernelVersions { get; set; } = new();

    [ObservableProperty]
    public partial string? SelectedKernelVersion { get; set; }

    partial void OnKernelHasUpdateChanged(bool value) => OnPropertyChanged(nameof(UpdateButtonText));

    [ObservableProperty]
    public partial string ElevatedHostStatus { get; set; } = "미실행";

    [ObservableProperty]
    public partial bool AutoCheckAppUpdate { get; set; } = true;

    [ObservableProperty]
    public partial string AppVersion { get; set; } = "알 수 없음";

    [ObservableProperty]
    public partial string LatestAppVersion { get; set; } = "—";

    [ObservableProperty]
    public partial bool AppHasUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsAppBusy { get; set; }

    [ObservableProperty]
    public partial string AppStatusText { get; set; } = "아직 검사 전";

    [ObservableProperty]
    public partial double AppProgress { get; set; }

    [ObservableProperty]
    public partial bool AppProgressVisible { get; set; }

    partial void OnAppHasUpdateChanged(bool value) => OnPropertyChanged(nameof(UpdateAppButtonText));

    public string UpdateAppButtonText => AppHasUpdate ? "한 번에 업데이트" : "업데이트";

    [ObservableProperty]
    public partial bool SilentStart { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool AutoQuitWithoutCore { get; set; }

    [ObservableProperty]
    public partial int AutoQuitWithoutCoreDelay { get; set; } = 30;

    [ObservableProperty]
    public partial bool AutoUpdateProfileOnStart { get; set; } = true;

    [ObservableProperty]
    public partial string DelayTestUrl { get; set; } = DelayTestSettings.DefaultUrl;

    [ObservableProperty]
    public partial string DelayTestTimeout { get; set; } = "3000";

    [ObservableProperty]
    public partial string DelayTestConcurrency { get; set; } = "16";

    [ObservableProperty]
    public partial string SubscriptionUserAgent { get; set; } = "";

    [ObservableProperty]
    public partial string SubscriptionTimeout { get; set; } = "60";

    /// <summary>코어/geodata 다운로드에 사용할 프록시 포트(코어 미실행 시). 빈 값은 미사용.</summary>
    [ObservableProperty]
    public partial string DownloadProxyPort { get; set; } = "";

    [ObservableProperty]
    public partial string HotkeyShowWindow { get; set; } = "";

    [ObservableProperty]
    public partial string HotkeyToggleSystemProxy { get; set; } = "";

    [ObservableProperty]
    public partial string HotkeyToggleTun { get; set; } = "";

    [ObservableProperty]
    public partial string HotkeyToggleMiniPanel { get; set; } = "";

    [ObservableProperty]
    public partial string HotkeyModeRule { get; set; } = "";

    [ObservableProperty]
    public partial string HotkeyModeGlobal { get; set; } = "";

    [ObservableProperty]
    public partial string HotkeyModeDirect { get; set; } = "";

    /// <summary>
    /// 단축키 바인딩 시도 후 결과 반환. 페이지가 충돌 안내 여부를 결정:
    /// 앱 내 다른 동작과 충돌하면 충돌 동작을 반환하고 아무 것도 변경하지 않음. 다른 프로그램이 점유 중이면 실패 반환.
    /// </summary>
    public async Task<HotkeyApplyResult> TryBindHotkeyAsync(string action, string text)
    {
        if (!Enum.TryParse<Resenz.App.Services.HotkeyAction>(action, out var hotkeyAction))
            return new HotkeyApplyResult(HotkeyApplyStatus.Invalid, null);

        var window = global::Resenz.App.App.Main;
        if (window is null) return new HotkeyApplyResult(HotkeyApplyStatus.Invalid, null);

        if (string.IsNullOrWhiteSpace(text))
        {
            await window.Hotkeys.UnbindAsync(hotkeyAction);
            await ReloadHotkeysAsync();
            StatusText = "단축키 해제됨";
            return new HotkeyApplyResult(HotkeyApplyStatus.Cleared, null);
        }

        if (!Resenz.App.Services.HotkeyParser.TryParse(text, out _, out _))
            return new HotkeyApplyResult(HotkeyApplyStatus.Invalid, null);

        var normalized = text.Trim();
        var conflict = await window.Hotkeys.FindConflictActionAsync(hotkeyAction, normalized);
        if (conflict is not null)
            return new HotkeyApplyResult(HotkeyApplyStatus.InternalConflict, conflict.Value);

        var ok = await window.Hotkeys.SetAsync(hotkeyAction, normalized);
        if (!ok) return new HotkeyApplyResult(HotkeyApplyStatus.Occupied, null);

        await ReloadHotkeysAsync();
        StatusText = $"단축키 설정함: {normalized}";
        return new HotkeyApplyResult(HotkeyApplyStatus.Ok, null);
    }

    /// <summary>사용자가 덮어쓰기 확인 후: 충돌 동작의 바인딩을 해제하고 이 단축키를 현재 동작에 바인딩.</summary>
    public async Task<bool> ResolveHotkeyConflictAsync(string action, string text, string conflictAction)
    {
        if (!Enum.TryParse<Resenz.App.Services.HotkeyAction>(action, out var target)) return false;
        if (!Enum.TryParse<Resenz.App.Services.HotkeyAction>(conflictAction, out var conflict)) return false;
        var window = global::Resenz.App.App.Main;
        if (window is null) return false;

        var normalized = text.Trim();
        await window.Hotkeys.UnbindAsync(conflict);
        var ok = await window.Hotkeys.SetAsync(target, normalized);
        await ReloadHotkeysAsync();
        StatusText = ok
            ? $"'{Resenz.App.Services.HotkeyManager.DisplayName(conflict)}'의 바인딩을 해제하고 {normalized}로 다시 설정함"
            : "단축키 등록 실패: 다른 프로그램이 점유 중일 수 있음";
        return ok;
    }

    private async Task ReloadHotkeysAsync()
    {
        HotkeyShowWindow = await _host.Settings.GetAsync("hotkey.ShowWindow") ?? "";
        HotkeyToggleSystemProxy = await _host.Settings.GetAsync("hotkey.ToggleSystemProxy") ?? "";
        HotkeyToggleTun = await _host.Settings.GetAsync("hotkey.ToggleTun") ?? "";
        HotkeyToggleMiniPanel = await _host.Settings.GetAsync("hotkey.ToggleMiniPanel") ?? "";
        HotkeyModeRule = await _host.Settings.GetAsync("hotkey.ModeRule") ?? "";
        HotkeyModeGlobal = await _host.Settings.GetAsync("hotkey.ModeGlobal") ?? "";
        HotkeyModeDirect = await _host.Settings.GetAsync("hotkey.ModeDirect") ?? "";
    }

    /// <summary>동작의 한국어 표시 이름(충돌 안내용).</summary>
    public static string HotkeyDisplayName(Resenz.App.Services.HotkeyAction action)
        => Resenz.App.Services.HotkeyManager.DisplayName(action);

    [ObservableProperty]
    public partial int GithubProxyIndex { get; set; }

    [ObservableProperty]
    public partial string GithubProxyCustom { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoCloseConnection { get; set; } = true;

    [ObservableProperty]
    public partial string TunStack { get; set; } = "mixed";

    [ObservableProperty]
    public partial bool TunAutoRoute { get; set; } = true;

    [ObservableProperty]
    public partial bool TunAutoRedirect { get; set; }

    [ObservableProperty]
    public partial bool TunAutoDetectInterface { get; set; } = true;

    [ObservableProperty]
    public partial bool TunStrictRoute { get; set; }

    [ObservableProperty]
    public partial string TunMtu { get; set; } = "";

    [ObservableProperty]
    public partial string TunDnsHijack { get; set; } = "";

    [ObservableProperty]
    public partial string TunRouteExcludeAddress { get; set; } = "";

    [ObservableProperty]
    public partial string PauseSsids { get; set; } = "";

    [ObservableProperty]
    public partial string SsidProfileMap { get; set; } = "";

    /// <summary>GitHub 프록시 내장 옵션, ComboBox 순서와 일치.</summary>
    public static readonly string[] GithubProxyBuiltins = { "auto", "direct" };

    public string UpdateButtonText => KernelHasUpdate ? "코어 업데이트" : "다시 다운로드";

    public SettingsViewModel(ResenzHost host)
    {
        _host = host;
        CorePath = host.Core.Paths.BinaryPath;
        DataDirectory = global::Resenz.Data.ResenzAppData.ResolveDirectory();
    }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            AutoStartCore = await _host.Settings.GetBoolAsync("core.autoStart", true);
            Theme = await _host.Settings.GetAsync("theme") ?? "default";
            NavigationStyle = await _host.Settings.GetIntAsync("ui.navigationStyle", 0);
            BackdropStyle = await _host.Settings.GetIntAsync("ui.backdropStyle", 0);
            MixedPort = (await _host.Settings.GetIntAsync("core.mixedPort", 7890)).ToString();
            ControllerPort = (await _host.Settings.GetIntAsync("core.controllerPort", 9090)).ToString();
            Mode = await _host.Settings.GetAsync("core.mode") ?? "rule";
            LogLevel = await _host.Settings.GetAsync("core.logLevel") ?? "info";
            AllowLan = await _host.Settings.GetBoolAsync("core.allowLan");
            Ipv6 = await _host.Settings.GetBoolAsync("core.ipv6", true);
            TunEnabled = await _host.Settings.GetBoolAsync("core.tun");
            StartupWithWindows = _host.Startup.IsEnabled();
            SystemProxyEnabled = _host.SystemProxy.IsEnabled();
            SystemProxyMode = await _host.Settings.GetAsync("ui.systemProxyMode") ?? "manual";
            KernelVersion = _host.KernelUpdate.GetInstalledVersion() ?? "미설치";
            ElevatedHostStatus = _host.Elevated.IsElevatedHostRunning ? "실행 중" : "미실행";

            SilentStart = await _host.Settings.GetBoolAsync("ui.silentStart");
            StartMinimized = await _host.Settings.GetBoolAsync("ui.startMinimized");
            AutoQuitWithoutCore = await _host.Settings.GetBoolAsync("ui.autoQuitWithoutCore");
            AutoQuitWithoutCoreDelay = await _host.Settings.GetIntAsync("ui.autoQuitWithoutCoreDelay", 30);
            AutoUpdateProfileOnStart = await _host.Settings.GetBoolAsync("profile.autoUpdate", true);

            AutoCheckAppUpdate = await _host.Settings.GetBoolAsync("ui.autoCheckAppUpdate", true);
            AppVersion = global::Resenz.Core.Services.AppUpdateService.GetCurrentVersion() + (
                _host.AppUpdate.IsInstalled ? "" : "(포터블)");
            ApplyCachedAppUpdate();

            var (dtUrl, dtTimeout, dtConc) = await DelayTestSettings.ReadAsync(_host.Settings);
            DelayTestUrl = dtUrl;
            DelayTestTimeout = dtTimeout.ToString();
            DelayTestConcurrency = dtConc.ToString();

            SubscriptionUserAgent = await _host.Settings.GetAsync("core.subscriptionUserAgent") ?? "";
            SubscriptionTimeout = (await _host.Settings.GetIntAsync("core.subscriptionTimeout", 60)).ToString();

            var dpp = await _host.Settings.GetIntAsync("core.downloadProxyPort", 0);
            DownloadProxyPort = dpp > 0 ? dpp.ToString() : "";

            HotkeyShowWindow = await _host.Settings.GetAsync("hotkey.ShowWindow") ?? "";
            HotkeyToggleSystemProxy = await _host.Settings.GetAsync("hotkey.ToggleSystemProxy") ?? "";
            HotkeyToggleTun = await _host.Settings.GetAsync("hotkey.ToggleTun") ?? "";
            HotkeyToggleMiniPanel = await _host.Settings.GetAsync("hotkey.ToggleMiniPanel") ?? "";
            HotkeyModeRule = await _host.Settings.GetAsync("hotkey.ModeRule") ?? "";
            HotkeyModeGlobal = await _host.Settings.GetAsync("hotkey.ModeGlobal") ?? "";
            HotkeyModeDirect = await _host.Settings.GetAsync("hotkey.ModeDirect") ?? "";

            var githubProxy = await _host.Settings.GetAsync("core.githubProxy") ?? "auto";
            var builtinIndex = Array.IndexOf(GithubProxyBuiltins, githubProxy);
            GithubProxyIndex = builtinIndex >= 0 ? builtinIndex : GithubProxyBuiltins.Length;
            GithubProxyCustom = builtinIndex >= 0 ? "" : githubProxy;

            AutoCloseConnection = await _host.Settings.GetBoolAsync("core.autoCloseConnection", true);

            TunStack = await _host.Settings.GetAsync("core.tunStack") ?? "mixed";
            TunAutoRoute = await _host.Settings.GetBoolAsync("core.tunAutoRoute", true);
            TunAutoRedirect = await _host.Settings.GetBoolAsync("core.tunAutoRedirect");
            TunAutoDetectInterface = await _host.Settings.GetBoolAsync("core.tunAutoDetectInterface", true);
            TunStrictRoute = await _host.Settings.GetBoolAsync("core.tunStrictRoute");
            TunMtu = await _host.Settings.GetAsync("core.tunMtuText") ?? "";
            TunDnsHijack = await _host.Settings.GetAsync("core.tunDnsHijack") ?? "";
            TunRouteExcludeAddress = await _host.Settings.GetAsync("core.tunRouteExcludeAddress") ?? "";
            PauseSsids = await _host.Settings.GetAsync("ui.pauseSsids") ?? "";
            SsidProfileMap = await _host.Settings.GetAsync("ui.ssidProfileMap") ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnThemeChanged(string value)
    {
        if (_loading) return;
        global::Resenz.App.App.ApplyTheme(value);
        _ = PersistAsync("theme", value, "테마 전환됨");
    }

    partial void OnNavigationStyleChanged(int value)
    {
        if (_loading) return;
        _ = _host.Settings.SetIntAsync("ui.navigationStyle", value);
        global::Resenz.App.App.Main?.ApplyNavigationStyle(value);
        StatusText = value == 1 ? "탐색이 상단으로 전환됨" : "탐색이 좌측으로 전환됨";
    }

    partial void OnBackdropStyleChanged(int value)
    {
        if (_loading) return;
        _ = _host.Settings.SetIntAsync("ui.backdropStyle", value);
        global::Resenz.App.App.Main?.ApplyBackdropStyle(value);
        StatusText = value switch
        {
            0 => "배경이 아크릴로 전환됨(아래 창 비침)",
            1 => "배경이 Mica로 전환됨",
            2 => "배경이 단색으로 전환됨",
            _ => "배경 소재 갱신됨",
        };
    }

    partial void OnAutoStartCoreChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.autoStart", value);
    }

    partial void OnStartupWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _host.Startup.SetEnabled(value);
        StatusText = value ? "부팅 자동 시작으로 설정됨" : "부팅 자동 시작 해제됨";
    }

    partial void OnSystemProxyEnabledChanged(bool value)
    {
        if (_loading || _applyingSwitch) return;
        _ = ApplySystemProxyAsync(value);
    }

    partial void OnSystemProxyModeChanged(string value)
    {
        if (_loading) return;
        var mode = string.Equals(value, "pac", StringComparison.OrdinalIgnoreCase) ? "pac" : "manual";
        _ = _host.Settings.SetAsync("ui.systemProxyMode", mode);
        StatusText = mode == "pac" ? "PAC 모드로 전환됨. 시스템 프록시를 껐다 켜야 적용" : "수동 프록시 모드로 전환됨";
    }

    partial void OnTunEnabledChanged(bool value)
    {
        if (_loading || _applyingSwitch) return;
        _ = ApplyTunAsync(value);
    }

    partial void OnAllowLanChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.allowLan", value);
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    partial void OnIpv6Changed(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.ipv6", value);
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    partial void OnLogLevelChanged(string value)
    {
        if (_loading) return;
        _ = ApplyLogLevelAsync(value);
    }

    partial void OnSilentStartChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("ui.silentStart", value);
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("ui.startMinimized", value);
    }

    partial void OnAutoQuitWithoutCoreChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("ui.autoQuitWithoutCore", value);
    }

    partial void OnAutoQuitWithoutCoreDelayChanged(int value)
    {
        if (_loading) return;
        if (value < 0) return;
        _ = _host.Settings.SetIntAsync("ui.autoQuitWithoutCoreDelay", value);
    }

    partial void OnAutoUpdateProfileOnStartChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("profile.autoUpdate", value);
    }

    partial void OnDelayTestUrlChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.delayTestUrl", value ?? "");
    }

    partial void OnDelayTestTimeoutChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var ms) && ms > 0)
            _ = _host.Settings.SetIntAsync("core.delayTestTimeout", ms);
    }

    partial void OnDelayTestConcurrencyChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var n) && n > 0)
            _ = _host.Settings.SetIntAsync("core.delayTestConcurrency", n);
    }

    partial void OnSubscriptionUserAgentChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.subscriptionUserAgent", value ?? "");
    }

    partial void OnSubscriptionTimeoutChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var s) && s > 0)
            _ = _host.Settings.SetIntAsync("core.subscriptionTimeout", s);
    }

    partial void OnDownloadProxyPortChanged(string value)
    {
        if (_loading) return;
        var port = int.TryParse(value, out var p) && p is > 0 and < 65536 ? p : 0;
        _ = _host.Settings.SetIntAsync("core.downloadProxyPort", port);
        Resenz.Core.Services.DownloadProxy.SetManualPort(port);
        StatusText = port > 0 ? $"다운로드 프록시를 127.0.0.1:{port}로 설정함" : "다운로드 프록시 꺼짐(코어 포트 또는 직접 연결 사용)";
    }

    partial void OnGithubProxyIndexChanged(int value)
    {
        if (_loading) return;
        PersistGithubProxy();
    }

    partial void OnGithubProxyCustomChanged(string value)
    {
        if (_loading) return;
        if (GithubProxyIndex != GithubProxyBuiltins.Length) return;
        PersistGithubProxy();
    }

    private void PersistGithubProxy()
    {
        var key = GithubProxyIndex >= 0 && GithubProxyIndex < GithubProxyBuiltins.Length
            ? GithubProxyBuiltins[GithubProxyIndex]
            : (string.IsNullOrWhiteSpace(GithubProxyCustom) ? "auto" : GithubProxyCustom.Trim());
        _ = _host.Settings.SetAsync("core.githubProxy", key);
    }

    partial void OnAutoCloseConnectionChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.autoCloseConnection", value);
    }

    partial void OnTunStackChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunStack", value);
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    partial void OnTunAutoRouteChanged(bool value) => PersistTunBool("core.tunAutoRoute", value);
    partial void OnTunAutoRedirectChanged(bool value) => PersistTunBool("core.tunAutoRedirect", value);
    partial void OnTunAutoDetectInterfaceChanged(bool value) => PersistTunBool("core.tunAutoDetectInterface", value);
    partial void OnTunStrictRouteChanged(bool value) => PersistTunBool("core.tunStrictRoute", value);

    private void PersistTunBool(string key, bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync(key, value);
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    partial void OnTunMtuChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunMtuText", value ?? "");
        var mtu = int.TryParse(value, out var m) && m > 0 ? m : 0;
        _ = _host.Settings.SetIntAsync("core.tunMtu", mtu);
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    partial void OnTunDnsHijackChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunDnsHijack", value ?? "");
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    partial void OnTunRouteExcludeAddressChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunRouteExcludeAddress", value ?? "");
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    partial void OnPauseSsidsChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("ui.pauseSsids", value ?? "");
        StatusText = "SSID 일시중지 규칙 저장됨";
    }

    partial void OnSsidProfileMapChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("ui.ssidProfileMap", value ?? "");
        StatusText = "SSID 구독 전환 규칙 저장됨";
    }

    partial void OnMixedPortChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var mp) && mp is > 0 and < 65536)
        {
            _ = _host.Settings.SetIntAsync("core.mixedPort", mp);
            StatusText = "포트 저장됨, 코어 재시작 후 적용";
        }
    }

    partial void OnControllerPortChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var cp) && cp is > 0 and < 65536)
        {
            _ = _host.Settings.SetIntAsync("core.controllerPort", cp);
            StatusText = "포트 저장됨, 코어 재시작 후 적용";
        }
    }

    private async Task PersistAsync(string key, string value, string message)
    {
        try
        {
            await _host.Settings.SetAsync(key, value);
            StatusText = message;
        }
        catch (Exception ex)
        {
            StatusText = $"저장 실패: {{ex.Message}";
        }
    }

    private async Task ApplySystemProxyAsync(bool enabled)
    {
        // ModeSelector로 일원화: 메인/미니 패널에 브로드캐스트 동기화하고 빠른 연속 조작 수렴 지원.
        var selector = global::Resenz.App.App.Main?.ModeSelector;
        if (selector is not null)
        {
            await selector.SetSystemProxyAsync(enabled);
            // selector.SystemProxyOn을 읽으면 안 됨: DispatcherQueue를 통해 비동기로 되돌아오는 값이라
            // 여기는 아직 옛 값일 수 있어 방금 전환한 상태를 덮어쓸 수 있음. 레지스트리의 실제 상태를 직접 읽음.
            _applyingSwitch = true;
            try
            {
                var on = _host.SystemProxy.IsEnabled();
                SystemProxyEnabled = on;
                StatusText = on ? "시스템 프록시 켜짐" : "시스템 프록시 꺼짐";
            }
            finally
            {
                _applyingSwitch = false;
            }
            return;
        }

        try
        {
            if (enabled)
            {
                var port = await _host.Settings.GetIntAsync("core.mixedPort", 7890);
                _host.SystemProxy.Enable($"127.0.0.1:{port}", "localhost;127.*;10.*;172.16.*;192.168.*");
                StatusText = $"시스템 프록시 켜짐(127.0.0.1:{port})";
            }
            else
            {
                _host.SystemProxy.Disable();
                StatusText = "시스템 프록시 꺼짐";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"시스템 프록시 설정 실패: {{ex.Message}";
        }

        AppSignals.RaiseSwitchesChanged();
    }

    private async Task ApplyTunAsync(bool enabled)
    {
        // ModeSelector로 일원화: TUN 핫스위치 시 코어를 재시작하지 않고 시스템 프록시를 건드리지 않으며 각 패널에 브로드캐스트 동기화.
        var selector = global::Resenz.App.App.Main?.ModeSelector;
        if (selector is not null)
        {
            await selector.SetTunAsync(enabled);
            // 시스템 프록시와 동일: selector.TunOn은 비동기 되돌림이므로 여기서는 실제 설정값을 읽음.
            _applyingSwitch = true;
            try
            {
                var on = await _host.Settings.GetBoolAsync("core.tun");
                TunEnabled = on;
                StatusText = on ? "TUN 켜짐" : "TUN 꺼짐";
            }
            finally
            {
                _applyingSwitch = false;
            }
            return;
        }

        try
        {
            await _host.Settings.SetBoolAsync("core.tun", enabled);
            if (enabled)
            {
                try
                {
                    await _host.KernelUpdate.EnsureWintunAsync();
                }
                catch
                {
                }
            }

            // 먼저 runtime.yaml을 다시 생성해야 함. 그러지 않으면 tun 섹션이 기록되지 않음.
            await _host.ApplyActiveProfileAsync();

            if (_host.Core.State is CoreState.Running or CoreState.Error)
            {
                StatusText = enabled ? "TUN 모드로 코어 재시작 중…" : "코어 재시작 중…";
                await _host.Core.RestartAsync();
                StatusText = _host.Core.State == CoreState.Running ? "TUN 설정 적용됨" : "코어 재시작 실패";
            }
            else
            {
                StatusText = "저장됨, 코어 재시작 후 적용";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"TUN 설정 실패: {{ex.Message}";
        }

        AppSignals.RaiseSwitchesChanged();
    }

    private async Task ApplyLogLevelAsync(string value)
    {
        await _host.Settings.SetAsync("core.logLevel", value);
        if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
        {
            try
            {
                await _host.Core.Api.PatchConfigsAsync(new Dictionary<string, object>
                {
                    ["log-level"] = value,
                });
                StatusText = "로그 레벨 적용됨";
                return;
            }
            catch
            {
            }
        }
        StatusText = "저장됨, 코어 재시작 후 적용";
    }

    [RelayCommand]
    private async Task FlushCachesAsync()
    {
        if (_host.Core.Api is null)
        {
            StatusText = "코어 미실행";
            return;
        }
        try
        {
            await _host.Core.Api.FlushDnsAsync();
            await _host.Core.Api.FlushFakeIpAsync();
            StatusText = "DNS 및 FakeIP 캐시 정리됨";
        }
        catch (Exception ex)
        {
            StatusText = $"정리 실패: {{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestartCoreAsync()
    {
        StatusText = "코어 재시작 중…";
        await _host.Core.RestartAsync();
        StatusText = _host.Core.State == CoreState.Running ? "코어 재시작됨" : "코어 재시작 실패";
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DataDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task CheckKernelAsync()
    {
        IsKernelBusy = true;
        KernelStatusText = "업데이트 검사 중…";
        try
        {
            var info = await _host.KernelUpdate.CheckAsync();
            KernelVersion = info.CurrentVersion ?? "미설치";
            LatestKernelVersion = info.LatestVersion ?? "—";
            KernelHasUpdate = info.HasUpdate;
            KernelStatusText = info.Error is not null
                ? $"검사 실패: {{info.Error}"
                : info.HasUpdate ? "새 버전 발견" : "이미 최신";
        }
        catch (Exception ex)
        {
            KernelStatusText = $"검사 실패: {{ex.Message}";
        }
        finally
        {
            IsKernelBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateKernelAsync()
    {
        IsKernelBusy = true;
        KernelProgressVisible = true;
        KernelProgress = 0;
        KernelStatusText = "코어 다운로드 중…";

        var wasRunning = _host.Core.State == CoreState.Running;
        if (wasRunning) await _host.Core.StopAsync();

        try
        {
            var target = LatestKernelVersion;
            if (string.IsNullOrEmpty(target) || target == "—")
            {
                var info = await _host.KernelUpdate.CheckAsync();
                target = info.LatestVersion;
            }
            if (string.IsNullOrEmpty(target))
            {
                KernelStatusText = "대상 버전 판단 불가";
                return;
            }

            var progress = new Progress<double>(p =>
            {
                KernelProgress = p * 100;
                KernelStatusText = $"코어 다운로드 중… {p * 100:0}%";
            });

            var ok = await _host.KernelUpdate.DownloadAndInstallAsync(target, progress);
            if (ok)
            {
                KernelVersion = _host.KernelUpdate.GetInstalledVersion() ?? target;
                KernelHasUpdate = false;
                KernelStatusText = $"코어 업데이트 완료: {target}. 지리 데이터 준비 중…";

                var geodataProgress = new Progress<double>(p =>
                    KernelStatusText = $"지리 데이터 다운로드 중… {p * 100:0}%");
                await _host.KernelUpdate.EnsureGeodataAsync(geodataProgress);

                if (await _host.Settings.GetBoolAsync("core.tun"))
                    await _host.KernelUpdate.EnsureWintunAsync();

                KernelStatusText = $"코어 업데이트 완료: {target}";
            }
            else
            {
                KernelStatusText = "코어 업데이트 실패";
            }
        }
        catch (Exception ex)
        {
            KernelStatusText = $"업데이트 실패: {{ex.Message}";
        }
        finally
        {
            KernelProgressVisible = false;
            IsKernelBusy = false;
            if (wasRunning) await _host.Core.StartAsync();
        }
    }

    [RelayCommand]
    private async Task LoadKernelVersionsAsync()
    {
        if (KernelVersions.Count > 0) return;
        try
        {
            var versions = await _host.KernelUpdate.ListVersionsAsync();
            KernelVersions.Clear();
            foreach (var v in versions) KernelVersions.Add(v);
            if (KernelVersions.Count > 0)
                SelectedKernelVersion = string.Equals(KernelVersions[0], LatestKernelVersion, StringComparison.OrdinalIgnoreCase)
                    ? KernelVersions[0]
                    : KernelVersions[0];
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task UpgradeToSelectedAsync()
    {
        var target = SelectedKernelVersion;
        if (string.IsNullOrEmpty(target)) return;

        IsKernelBusy = true;
        KernelProgressVisible = true;
        KernelProgress = 0;
        KernelStatusText = $"코어 다운로드 중 {target}…";

        var wasRunning = _host.Core.State == CoreState.Running;
        if (wasRunning) await _host.Core.StopAsync();

        try
        {
            var progress = new Progress<double>(p =>
            {
                KernelProgress = p * 100;
                KernelStatusText = $"코어 다운로드 중… {p * 100:0}%";
            });

            var ok = await _host.KernelUpdate.DownloadAndInstallAsync(target, progress);
            if (ok)
            {
                KernelVersion = _host.KernelUpdate.GetInstalledVersion() ?? target;
                LatestKernelVersion = target;
                KernelHasUpdate = false;
                KernelStatusText = $"코어 업데이트 완료: {target}. 지리 데이터 준비 중…";

                var geodataProgress = new Progress<double>(p =>
                    KernelStatusText = $"지리 데이터 다운로드 중… {p * 100:0}%");
                await _host.KernelUpdate.EnsureGeodataAsync(geodataProgress);

                if (await _host.Settings.GetBoolAsync("core.tun"))
                    await _host.KernelUpdate.EnsureWintunAsync();

                KernelStatusText = $"코어 업데이트 완료: {target}";
            }
            else
            {
                KernelStatusText = "코어 업데이트 실패";
            }
        }
        catch (Exception ex)
        {
            KernelStatusText = $"업데이트 실패: {{ex.Message}";
        }
        finally
        {
            KernelProgressVisible = false;
            IsKernelBusy = false;
            if (wasRunning) await _host.Core.StartAsync();
        }
    }

    [RelayCommand]
    private async Task LaunchElevatedHostAsync()
    {
        try
        {
            var ok = _host.Elevated.LaunchElevatedHost();
            await Task.Delay(1200);
            ElevatedHostStatus = _host.Elevated.IsElevatedHostRunning ? "실행 중" : "미실행";
            StatusText = ok ? "관리자 권한으로 상승 호스트 기동 요청됨" : "상승 호스트 기동 실패";
        }
        catch (Exception ex)
        {
            StatusText = $"상승 호스트 기동 실패: {{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ShutdownElevatedHostAsync()
    {
        try
        {
            await _host.Elevated.ShutdownAsync();
            ElevatedHostStatus = "미실행";
            StatusText = "상승 호스트 종료됨";
        }
        catch (Exception ex)
        {
            StatusText = $"상승 호스트 종료 실패: {{ex.Message}";
        }
    }

    partial void OnAutoCheckAppUpdateChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("ui.autoCheckAppUpdate", value);
        StatusText = value ? "시작 시 업데이트 자동 검사 켜짐" : "시작 시 업데이트 자동 검사 꺼짐";
    }

    [RelayCommand]
    private async Task CheckAppAsync()
    {
        IsAppBusy = true;
        AppStatusText = "업데이트 검사 중…";
        try
        {
            var info = await _host.AppUpdate.CheckAsync();
            ApplyCachedAppUpdate();
            AppStatusText = info.Error is not null
                ? $"검사 실패: {{info.Error}"
                : info.HasUpdate ? "새 버전 발견" : "이미 최신";
        }
        catch (Exception ex)
        {
            AppStatusText = $"검사 실패: {{ex.Message}";
        }
        finally
        {
            IsAppBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAppAsync()
    {
        var main = global::Resenz.App.App.Main;
        if (main is null) return;

        IsAppBusy = true;
        AppProgress = 0;
        AppProgressVisible = true;
        AppStatusText = "업데이트 다운로드 중…";
        try
        {
            var progress = new Progress<double>(p => AppProgress = p);
            var info = await _host.AppUpdate.PrepareUpdateAsync(progress);
            if (info.Error is not null)
            {
                AppStatusText = $"업데이트 실패: {{info.Error}";
                return;
            }

            AppStatusText = "업데이트 적용 중…";
            // 종료 전에 코어/TUN/시스템 프록시를 먼저 끄고 업데이트 스크립트를 띄운 뒤 종료. 스크립트 완료 후 자동 재시작 및 코어 기동.
            global::Resenz.App.App.Main?.ExitForUpdate();
        }
        catch (Exception ex)
        {
            AppStatusText = $"업데이트 실패: {{ex.Message}";
        }
        finally
        {
            IsAppBusy = false;
            AppProgressVisible = false;
        }
    }

    private void ApplyCachedAppUpdate()
    {
        var info = global::Resenz.Core.Services.AppUpdateService.LastResult;
        if (info is null) return;
        LatestAppVersion = info.LatestVersion ?? "—";
        AppHasUpdate = info.HasUpdate;
        if (info.Error is not null) AppStatusText = $"검사 실패: {{info.Error}";
    }
}
