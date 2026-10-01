using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Resenz.Core.Models;
using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private const int RateWindow = 60;

    private readonly ResenzHost _host;
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private readonly Queue<double> _upHistory = new();
    private readonly Queue<double> _downHistory = new();

    [ObservableProperty]
    public partial string UpRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string DownRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string UpTotal { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DownTotal { get; set; } = "0 B";

    [ObservableProperty]
    public partial string Memory { get; set; } = "0 B";

    [ObservableProperty]
    public partial int ConnectionCount { get; set; }

    [ObservableProperty]
    public partial string CoreStateText { get; set; } = "정지됨";

    [ObservableProperty]
    public partial string CoreVersion { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string Mode { get; set; } = "rule";

    [ObservableProperty]
    public partial string MixedPort { get; set; } = "—";

    [ObservableProperty]
    public partial string StateKey { get; set; } = "stopped";

    [ObservableProperty]
    public partial string ActiveProfileName { get; set; } = "구독 미선택";

    [ObservableProperty]
    public partial string ProfileUsageText { get; set; } = "";

    [ObservableProperty]
    public partial double ProfileUsagePercent { get; set; }

    [ObservableProperty]
    public partial bool HasProfileUsage { get; set; }

    [ObservableProperty]
    public partial string ProfileExpireText { get; set; } = "";

    [ObservableProperty]
    public partial string GroupSummary { get; set; } = "—";

    [ObservableProperty]
    public partial int NodeCount { get; set; }

    [ObservableProperty]
    public partial string SystemProxyText { get; set; } = "꺼짐";

    [ObservableProperty]
    public partial string TunText { get; set; } = "꺼짐";

    [ObservableProperty]
    public partial string ExitIp { get; set; } = "—";

    [ObservableProperty]
    public partial string ExitCountry { get; set; } = "";

    /// <summary>출구 IP 소재지 국기 emoji(빈 값은 없음).</summary>
    [ObservableProperty]
    public partial string ExitFlag { get; set; } = "";

    [ObservableProperty]
    public partial bool HasExitIp { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingExit { get; set; }

    [ObservableProperty]
    public partial bool IsTestingAll { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<double> UpPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<double> DownPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial double MaxRate { get; set; } = 1;

    public ObservableCollection<DashboardGroupViewModel> Groups { get; } = new();
    public ObservableCollection<LatencyTargetViewModel> Targets { get; } = new();

    public string GroupSummaryFallback => "—";

    private readonly EventHandler<CoreStateChanged> _onState;
    private readonly EventHandler<TrafficSnapshot> _onTraffic;
    private readonly EventHandler<MemorySnapshot> _onMemory;
    private readonly EventHandler<ConnectionsSnapshot> _onConnections;
    private readonly EventHandler _onProxiesReloaded;
    private readonly EventHandler<bool> _onMainWindowVisibility;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string ToggleText => IsRunning ? "정지" : "시작";

    public string ToggleGlyph => IsRunning ? "\uE71A" : "\uE768";

    public string NodeCountText => $"{NodeCount}개 노드";

    partial void OnNodeCountChanged(int value) => OnPropertyChanged(nameof(NodeCountText));

    public DashboardViewModel(ResenzHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _core = host.Core;
        _dispatcher = dispatcher;

        Targets.Add(new LatencyTargetViewModel("Google", "https://www.gstatic.com/generate_204"));
        Targets.Add(new LatencyTargetViewModel("Cloudflare", "https://cloudflare.com/cdn-cgi/trace"));
        Targets.Add(new LatencyTargetViewModel("GitHub", "https://github.com"));
        Targets.Add(new LatencyTargetViewModel("YouTube", "https://www.youtube.com"));

        _onState = (_, e) => _dispatcher.TryEnqueue(() => OnStateChanged(e));
        _onTraffic = (_, t) => _dispatcher.TryEnqueue(() => OnTraffic(t));
        _onMemory = (_, m) => _dispatcher.TryEnqueue(() => Memory = Format.Bytes(m.InUse));
        _onConnections = (_, c) => _dispatcher.TryEnqueue(() => ConnectionCount = c.Connections.Count);
        _onProxiesReloaded = (_, _) => _dispatcher.TryEnqueue(() => _ = LoadAllAsync());
        _onMainWindowVisibility = (_, visible) => OnMainWindowVisibility(null, visible);
    }

    private bool _attached;
    private bool _mainWindowVisible = true;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;

        _core.StateChanged += _onState;
        _core.TrafficUpdated += _onTraffic;
        _core.MemoryUpdated += _onMemory;
        _core.ConnectionsUpdated += _onConnections;
        AppSignals.ProxiesChanged += _onProxiesReloaded;
        AppSignals.MainWindowVisibilityChanged += _onMainWindowVisibility;

        // 폴백: 코어 방금 시작으로 프록시 미준비로 검출 실패 시 주기 재시도로 출구 IP가 나올 때까지 진행.
        _refreshTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _refreshTimer.Tick += (_, _) => _ = RefreshNetworkAsync();
        if (_mainWindowVisible) _refreshTimer.Start();

        OnStateChanged(new CoreStateChanged(_core.State, _core.Version, _core.LastError));
        if (_mainWindowVisible) _ = RefreshNetworkAsync();
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _refreshTimer;

    private void OnMainWindowVisibility(object? sender, bool visible)
    {
        // 메인 창 숨김(트레이 / 미니 패널 전용) 시 홈 폴링과 네트워크 탐지를 일시중지해 불필요한 요청과 할당을 피함.
        _mainWindowVisible = visible;
        _dispatcher.TryEnqueue(() =>
        {
            if (_refreshTimer is null) return;
            if (visible)
            {
                _refreshTimer.Start();
                _ = RefreshNetworkAsync();
            }
            else
            {
                _refreshTimer.Stop();
            }
        });
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;

        _refreshTimer?.Stop();
        _refreshTimer = null;

        _core.StateChanged -= _onState;
        _core.TrafficUpdated -= _onTraffic;
        _core.MemoryUpdated -= _onMemory;
        _core.ConnectionsUpdated -= _onConnections;
        AppSignals.ProxiesChanged -= _onProxiesReloaded;
        AppSignals.MainWindowVisibilityChanged -= _onMainWindowVisibility;
    }

    private void OnStateChanged(CoreStateChanged e)
    {
        IsRunning = e.State == CoreState.Running;
        CoreStateText = e.State switch
        {
            CoreState.Running => "실행 중",
            CoreState.Starting => "시작 중",
            CoreState.Stopping => "정지 중",
            CoreState.Error => "이상",
            _ => "정지됨",
        };
        CoreVersion = e.Version ?? "";
        ErrorMessage = e.Error;
        StateKey = e.State switch
        {
            CoreState.Running => "running",
            CoreState.Starting => "starting",
            CoreState.Stopping => "stopping",
            CoreState.Error => "error",
            _ => "stopped",
        };
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(ToggleGlyph));
        if (IsRunning)
        {
            _ = LoadAllAsync();
            // 코어 실행 후 출구 IP / 지연 검출을 한 번 보충(30초 중복 제거 포함, 중복 요청 걱정 불필요).
            _ = RefreshNetworkAsync();
        }
        else ClearLiveData();
    }

    private void ClearLiveData()
    {
        Groups.Clear();
        GroupSummary = "—";
        NodeCount = 0;
        _upHistory.Clear();
        _downHistory.Clear();
        UpPoints = Array.Empty<double>();
        DownPoints = Array.Empty<double>();
    }

    private DateTimeOffset _lastExitCheck = DateTimeOffset.MinValue;

    /// <summary>출구 IP와 대상 지연 개요 새로고침. 홈에 들어올 때마다 출구 IP를 새로고침해 낡은 값 방지.</summary>
    public async Task RefreshNetworkAsync()
    {
        await CheckExitIpOnceAsync().ConfigureAwait(false);
        await TestTargetsOnceAsync().ConfigureAwait(false);
    }

    private async Task CheckExitIpOnceAsync()
    {
        // 코어 실행 중에만 검출: 이때만 프록시를 거치므로 출구 IP가 실제 프록시 출구임.
        // 실패 시 타임스탬프를 갱신하지 않아 DispatcherTimer(5초)가 성공할 때까지 계속 재시도.
        var now = DateTimeOffset.Now;
        if (IsCheckingExit || _core.Api is null || _core.State != CoreState.Running
            || now - _lastExitCheck < TimeSpan.FromSeconds(3)) return;
        IsCheckingExit = true;
        try
        {
            var ip = await QueryExitIpAsync().ConfigureAwait(false);
            var code = await QueryCountryCodeAsync(ip).ConfigureAwait(false);
            var country = CountryNames.GetName(code);
            if (!string.IsNullOrEmpty(ip))
            {
                _lastExitCheck = now;
                _dispatcher.TryEnqueue(() =>
                {
                    ExitIp = ip;
                    ExitCountry = country;
                    ExitFlag = CountryNames.GetFlag(code);
                    HasExitIp = true;
                });
            }
        }
        catch
        {
        }
        finally
        {
            _dispatcher.TryEnqueue(() => IsCheckingExit = false);
        }
    }

    /// <summary>국가 코드 조회: 외부 geo 서비스로 직접 조회(코어 geoip 인터페이스를 쓰면 404를 하얗게 기다리게 되므로 회피).</summary>
    private async Task<string> QueryCountryCodeAsync(string ip)
    {
        var (_, _, externalCode) = await QueryGeoAsync(ip).ConfigureAwait(false);
        return externalCode;
    }

    private bool _targetsChecked;
    private async Task TestTargetsOnceAsync()
    {
        if (_targetsChecked || IsTestingAll) return;
        _targetsChecked = true;
        IsTestingAll = true;
        try
        {
            foreach (var target in Targets)
                _dispatcher.TryEnqueue(() => target.IsTesting = true);

            // 노드 측정과 동일 정책: 동시성 제어 + 대상마다 여러 번 중 최솟값.
            await DelayTester.MeasureAllHttpAsync(
                Targets,
                t => t.Url,
                MakeLatencyClient,
                concurrency: 4,
                timeoutMs: 6000,
                (target, ms) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        target.SetResult(ms);
                        target.IsTesting = false;
                    });
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
        finally
        {
            _dispatcher.TryEnqueue(() => IsTestingAll = false);
        }
    }

    private async Task<string> QueryExitIpAsync()
    {
        using var client = MakeNetworkClient();
        foreach (var url in new[] { "https://api.ipify.org", "https://ipv4.icanhazip.com", "https://ifconfig.me/ip" })
        {
            try
            {
                var text = (await client.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch
            {
            }
        }
        throw new InvalidOperationException("모든 IP 소스 접근 불가");
    }

    private async Task<(string Country, string Region, string CountryCode)> QueryGeoAsync(string ip)
    {
        try
        {
            using var client = MakeNetworkClient();
            var json = await client.GetStringAsync($"https://ipwho.is/{ip}").ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.GetBoolean() == false) return ("", "", "");
            var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
            var region = root.TryGetProperty("region", out var r) ? r.GetString() : null;
            var code = root.TryGetProperty("country_code", out var cc) ? cc.GetString() : null;
            return (country ?? "", region ?? "", code ?? "");
        }
        catch
        {
            return ("", "", "");
        }
    }

    private System.Net.Http.HttpClient MakeNetworkClient()
    {
        var handler = new System.Net.Http.HttpClientHandler { Proxy = DownloadProxy.Create() };
        return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>지연 측정 클라이언트: 현재 프록시 경유(출구 IP 검출과 같은 경로)로 엔드투엔드 실제 지연을 측정.</summary>
    private static System.Net.Http.HttpClient MakeLatencyClient()
    {
        var handler = new System.Net.Http.HttpClientHandler { Proxy = DownloadProxy.Create() };
        return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private async Task LoadAllAsync()
    {
        await RefreshConfigAsync();
        await RefreshProfileAsync();
        await RefreshGroupsAsync();
        RefreshToggles();
    }

    private void RefreshToggles()
    {
        try
        {
            var enabled = _host.SystemProxy.IsEnabled();
            _dispatcher.TryEnqueue(() => SystemProxyText = enabled ? "켜짐" : "꺼짐");
        }
        catch
        {
        }
    }

    private async Task RefreshConfigAsync()
    {
        if (_core.Api is null || _core.State != CoreState.Running) return;
        try
        {
            var config = await _core.Api.GetConfigsAsync();
            if (config is null) return;
            _dispatcher.TryEnqueue(() =>
            {
                Mode = config.Mode;
                MixedPort = config.MixedPort > 0 ? config.MixedPort.ToString() : "—";
            });
        }
        catch
        {
        }
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            var active = await _host.Profiles.GetActiveAsync();
            if (active is null)
            {
                ActiveProfileName = "구독 미선택";
                HasProfileUsage = false;
                ProfileUsageText = "";
                ProfileExpireText = "";
                return;
            }

            ActiveProfileName = active.Name;
            var info = SubscriptionUsage.Parse(active.SubscriptionUserInfo);
            if (info is null)
            {
                HasProfileUsage = false;
                ProfileUsageText = "";
                ProfileExpireText = "";
                return;
            }

            ProfileUsageText = info.Total > 0
                ? $"{Format.Bytes(info.Used)} / {Format.Bytes(info.Total)}"
                : $"↑ {Format.Bytes(info.Upload)}  ↓ {Format.Bytes(info.Download)}";
            ProfileUsagePercent = info.Total > 0 ? Math.Clamp(info.Used * 100.0 / info.Total, 0, 100) : 0;
            HasProfileUsage = info.Total > 0;
            ProfileExpireText = info.Expire <= 0
                ? "장기 유효"
                : $"만료 {DateTimeOffset.FromUnixTimeSeconds(info.Expire).LocalDateTime:yyyy-MM-dd}";
        }
        catch
        {
        }
    }

    private async Task RefreshGroupsAsync()
    {
        if (_core.Api is null) return;
        try
        {
            var proxies = await _core.Api.GetProxiesAsync();

            var declared = MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath);
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < declared.Count; i++) rank[declared[i]] = i;

            _dispatcher.TryEnqueue(() =>
            {
                Groups.Clear();
                var nodes = new HashSet<string>(StringComparer.Ordinal);

                var ordered = proxies.Values
                    .Where(p => p.All is { Count: > 0 } && !IsGlobalGroup(p.Name))
                    .OrderBy(p => rank.TryGetValue(p.Name, out var r) ? r : int.MaxValue);

                foreach (var p in ordered)
                {
                    var members = new List<ProxyItemViewModel>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in p.All!)
                    {
                        if (!proxies.TryGetValue(member, out var node)) continue;
                        if (!IsCountableNode(node)) continue;
                        if (!seen.Add(node.Name)) continue;
                        members.Add(new ProxyItemViewModel(node.Name, node.Type, node.Alive, node.Delay, p.Now == member, groupName: p.Name));
                        nodes.Add(node.Name);
                    }

                    var selected = members.FirstOrDefault(n => n.IsSelected)?.Name ?? p.Now ?? "—";
                    Groups.Add(new DashboardGroupViewModel(p.Name, p.Type, selected, members));
                }

                NodeCount = nodes.Count;
                GroupSummary = Groups.Count == 0
                    ? "—"
                    : $"{Groups.Count}개 프록시 그룹 · {NodeCount}개 노드";
            });
        }
        catch
        {
        }
    }

    private static bool IsGlobalGroup(string name) =>
        string.Equals(name, "GLOBAL", StringComparison.OrdinalIgnoreCase);

    private static bool IsCountableNode(ProxyItem p) =>
        !string.IsNullOrEmpty(p.Type) && p.Type.ToLowerInvariant() switch
        {
            "selector" or "urltest" or "fallback" or "loadbalance" or "relay"
                or "direct" or "reject" or "rejectdrop" or "pass" or "dns"
                or "compatible" or "global" => false,
            _ => true,
        };

    private void OnTraffic(TrafficSnapshot t)
    {
        UpRate = Format.Rate(t.Up);
        DownRate = Format.Rate(t.Down);
        UpTotal = Format.Bytes(t.UpTotal);
        DownTotal = Format.Bytes(t.DownTotal);

        Push(_upHistory, t.Up);
        Push(_downHistory, t.Down);

        UpPoints = _upHistory.ToArray();
        DownPoints = _downHistory.ToArray();
        MaxRate = Math.Max(1, Math.Max(_upHistory.DefaultIfEmpty(0).Max(), _downHistory.DefaultIfEmpty(0).Max()));
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > RateWindow) queue.Dequeue();
    }

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        if (IsRunning) await _core.StopAsync();
        else await _core.StartAsync();
    }

    [RelayCommand]
    private async Task RestartCoreAsync()
    {
        await _core.RestartAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadAllAsync();
        await RefreshNetworkAsync();
    }
}

public sealed class DashboardGroupViewModel
{
    public string Name { get; }
    public string TypeText { get; }
    public string SelectedNode { get; }
    public IReadOnlyList<ProxyItemViewModel> Nodes { get; }
    public string CountText { get; }

    public DashboardGroupViewModel(string name, string type, string selectedNode, IReadOnlyList<ProxyItemViewModel> nodes)
    {
        Name = name;
        SelectedNode = selectedNode;
        Nodes = nodes;
        CountText = $"{nodes.Count}개 노드";
        TypeText = string.IsNullOrEmpty(type) ? "프록시 그룹" : type.ToLowerInvariant() switch
        {
            "selector" => "선택자",
            "urltest" => "자동 측정",
            "fallback" => "장애 조치",
            "loadbalance" => "로드 밸런싱",
            "relay" => "체인 프록시",
            var t => t,
        };
    }
}
