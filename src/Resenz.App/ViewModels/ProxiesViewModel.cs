using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Resenz.Core.Models;
using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

public sealed partial class ProxyGroupViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly ProxiesViewModel _owner;
    private IReadOnlyList<ProxyItemViewModel> _all = Array.Empty<ProxyItemViewModel>();

    public string Name { get; }
    public string Type { get; }
    public string TypeText { get; }

    [ObservableProperty]
    public partial ObservableCollection<ProxyItemViewModel> Nodes { get; set; } = new();

    [ObservableProperty]
    public partial string NowText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    public ProxyGroupViewModel(string name, string type, ICoreManager core, ProxiesViewModel owner)
    {
        Name = name;
        Type = type;
        _core = core;
        _owner = owner;
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

    public void SetNodes(IReadOnlyList<ProxyItemViewModel> nodes, string? now, int sortIndex)
    {
        _all = nodes;
        NowText = string.IsNullOrEmpty(now) ? "" : now;
        SortIndex = sortIndex;
        foreach (var n in _all)
        {
            n.SelectRequested = SelectNode;
            // 그룹 전체 재측정 대신 노드 하나씩 개별 측정. 단일 측정 완료 후 브로드캐스트해 미니 패널이 지연을 동기화.
            n.TestRequested = node => _ = TestNodeAsync(node, broadcast: true);
        }
        ApplySort();
    }

    public void ApplySort()
    {
        // 선택 상태 기준 정렬 금지: 선택 항목이 맨 앞으로 점프하면 노드 클릭 때 목록이 재배열되어 사용자가 위치감을 잃음.
        var sorted = SortIndex switch
        {
            1 => _all
                    .OrderBy(n => n.IsAlive ? 0 : 1)
                    .ThenBy(n => n.Delay is null or < 0 ? int.MaxValue : n.Delay)
                    .ToList(),
            2 => _all.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => _all.ToList(),
        };

        Nodes.Clear();
        foreach (var n in sorted) Nodes.Add(n);
    }

    partial void OnSortIndexChanged(int value) => ApplySort();

    public async void SelectNode(ProxyItemViewModel? node)
    {
        if (node is null || _core.Api is null) return;
        try
        {
            await _core.Api.SelectProxyAsync(Name, node.Name).ConfigureAwait(false);
            if (_owner.AutoCloseConnection)
                await _owner.CloseConnectionsSafeAsync().ConfigureAwait(false);
            await ProxySelectionStore
                .RememberAsync(global::Resenz.App.AppHost.Host.Settings, Name, node.Name)
                .ConfigureAwait(false);
            _owner.Dispatcher.TryEnqueue(() =>
            {
                foreach (var n in _all) n.SetSelected(n.Name == node.Name);
                NowText = node.Name;
                _owner.ReportStatus($"'{node.Name}' 노드로 전환함");
                // 이 페이지는 선택 상태를 제자리 갱신했으므로 브로드캐스트 때 자기 자신은 새로고침을 건너뛰어 전체 페이지 재구축 방지.
                _owner.RaiseProxiesChangedExceptSelf();
            });
        }
        catch (Exception ex)
        {
            _owner.ReportStatus($"전환 실패: {{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (_core.Api is null) return;
        IsBusy = true;
        try
        {
            // DelayTester로 일원화: 동시성 제어 + 노드마다 여러 번 측정해 최솟값 사용. 전체 측정/단일 측정과 동일.
            _owner.Dispatcher.TryEnqueue(() => { foreach (var n in _all) n.IsTesting = true; });
            var names = _all.Select(n => n.Name).ToList();
            await DelayTester.MeasureAllAsync(
                _core.Api, names, _owner.DelayTestUrl, _owner.DelayTestTimeoutMs, _owner.DelayTestConcurrency,
                (name, delay) =>
                {
                    _owner.Dispatcher.TryEnqueue(() =>
                    {
                        _owner.SyncNodeDelay(name, delay, delay > 0);
                    });
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

            _owner.Dispatcher.TryEnqueue(() =>
            {
                foreach (var n in _all) n.IsTesting = false;
                if (SortIndex == 1) ApplySort();
                _owner.ReportStatus($"{Name} 측정 완료");
                IsBusy = false;
                // 그룹 전체 측정 종료 후 한 번 일괄 브로드캐스트해 미니 패널이 지연을 동기화.
                _owner.RaiseProxiesChangedExceptSelf();
            });
        }
        catch (Exception ex)
        {
            _owner.Dispatcher.TryEnqueue(() =>
            {
                foreach (var n in _all) n.IsTesting = false;
                IsBusy = false;
                _owner.ReportStatus($"측정 실패: {{ex.Message}");
            });
        }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    /// <summary>그룹 내 전체 노드(정렬에 따라 변하지 않음).</summary>
    public IReadOnlyList<ProxyItemViewModel> AllNodes => _all;

    public void SetBusy(bool value) => IsBusy = value;

    /// <summary>노드 하나를 측정하고 지연을 모든 그룹의 같은 이름 노드에 동기화.</summary>
    public async Task TestNodeAsync(ProxyItemViewModel node, bool broadcast = false)
    {
        if (_core.Api is null || node.IsTesting) return;
        node.IsTesting = true;
        var delay = await DelayTester
            .MeasureAsync(_core.Api, node.Name, _owner.DelayTestUrl, _owner.DelayTestTimeoutMs)
            .ConfigureAwait(false);
        _owner.Dispatcher.TryEnqueue(() =>
        {
            node.IsTesting = false;
            node.Delay = delay;
            node.IsAlive = delay > 0;
            _owner.SyncNodeDelay(node.Name, delay, delay > 0);
            if (SortIndex == 1) ApplySort();
            // 단일 노드 측정 시 한 번 브로드캐스트해 미니 패널이 지연을 동기화(그룹 전체 측정은 TestAsync가 일괄 브로드캐스트).
            if (broadcast) _owner.RaiseProxiesChangedExceptSelf();
        });
    }
}

public sealed partial class ProxiesViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private bool _loaded;
    private IReadOnlyDictionary<string, ProxyItem> _proxies =
        new Dictionary<string, ProxyItem>();
    private readonly EventHandler<string> _onModeChanged;
    private readonly EventHandler<CoreStateChanged> _onStateChanged;

    public DispatcherQueue Dispatcher => _dispatcher;

    public ObservableCollection<ProxyGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "아직 불러오지 않음";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial bool IsGlobalMode { get; set; }

    /// <summary>현재 지연 측정 파라미터(그룹 내 노드 측정이 읽어 감).</summary>
    public string DelayTestUrl { get; private set; } = DelayTestSettings.DefaultUrl;
    public int DelayTestTimeoutMs { get; private set; } = DelayTestSettings.DefaultTimeoutMs;
    public int DelayTestConcurrency { get; private set; } = DelayTestSettings.DefaultConcurrency;

    /// <summary>노드 전환 후 오래된 연결 자동 종료 여부.</summary>
    public bool AutoCloseConnection { get; private set; } = true;

    public async Task ReloadDelayTestAsync()
    {
        var settings = global::Resenz.App.AppHost.Host.Settings;
        var (url, timeout, concurrency) = await DelayTestSettings.ReadAsync(settings).ConfigureAwait(false);
        DelayTestUrl = url;
        DelayTestTimeoutMs = timeout;
        DelayTestConcurrency = concurrency;
        AutoCloseConnection = await settings.GetBoolAsync("core.autoCloseConnection", true).ConfigureAwait(false);
    }

    public ProxiesViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        IsGlobalMode = ModeState.IsGlobal;

        _onModeChanged = (_, mode) => ApplyMode(mode);
        _onStateChanged = (_, e) =>
        {
            if (e.State != CoreState.Running) _loaded = false;
            else _ = AutoReloadAsync();
        };
        _onSignals = (_, _) =>
        {
            // 스스로 보낸 브로드캐스트(노드 전환 등)는 제자리 갱신되었으므로 전체 페이지 재구축 생략.
            if (_suppressSelfReload) return;
            _dispatcher.TryEnqueue(() => _ = LoadAsync(force: true));
        };
    }

    private readonly EventHandler _onSignals;
    private bool _suppressSelfReload;

    /// <summary>프록시 데이터 변경을 브로드캐스트하되 이 페이지는 이번 브로드캐스트를 무시(이 페이지가 이미 제자리 갱신된 경우용).</summary>
    public void RaiseProxiesChangedExceptSelf()
    {
        _suppressSelfReload = true;
        try
        {
            AppSignals.RaiseProxiesChanged();
        }
        finally
        {
            _suppressSelfReload = false;
        }
    }

    private bool _attached;
    private bool _loadingSort;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        ModeState.Changed += _onModeChanged;
        _core.StateChanged += _onStateChanged;
        AppSignals.ProxiesChanged += _onSignals;
        _ = LoadSortAsync();
        _ = ReloadDelayTestAsync();
    }

    private async Task LoadSortAsync()
    {
        try
        {
            var settings = global::Resenz.App.AppHost.Host.Settings;
            var saved = await settings.GetIntAsync("ui.proxySort", 0).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                _loadingSort = true;
                try
                {
                    SortIndex = Math.Clamp(saved, 0, 2);
                }
                finally
                {
                    _loadingSort = false;
                }
            });
        }
        catch
        {
        }
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        ModeState.Changed -= _onModeChanged;
        _core.StateChanged -= _onStateChanged;
        AppSignals.ProxiesChanged -= _onSignals;
    }

    public void ReportStatus(string text) => _dispatcher.TryEnqueue(() => StatusText = text);

    public void ApplyMode(string mode)
    {
        var isGlobal = string.Equals(mode, "global", StringComparison.OrdinalIgnoreCase);
        _dispatcher.TryEnqueue(() =>
        {
            if (IsGlobalMode == isGlobal) return;
            IsGlobalMode = isGlobal;
            if (_loaded) Rebuild();
        });
    }

    private async Task AutoReloadAsync()
    {
        await Task.Delay(500);
        await LoadAsync(force: true);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_core.Api is null || _core.State != CoreState.Running)
        {
            StatusText = "코어 미실행";
            return;
        }
        if (_loaded && !force) return;

        IsBusy = true;
        try
        {
            _proxies = await _core.Api.GetProxiesAsync().ConfigureAwait(false);

            _dispatcher.TryEnqueue(() =>
            {
                Rebuild();
                _loaded = true;
            });
        }
        catch (Exception ex)
        {
            StatusText = $"로드 실패: {{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Rebuild()
    {
        Groups.Clear();

        var declared = MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declared.Count; i++) rank[declared[i]] = i;

        var ordered = _proxies.Values
            .Where(p => p.All is { Count: > 0 })
            .OrderBy(p => rank.TryGetValue(p.Name, out var r) ? r : int.MaxValue)
            .ToList();

        foreach (var p in ordered)
        {
            if (!IsGlobalMode && string.Equals(p.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
                continue;

            var group = new ProxyGroupViewModel(p.Name, p.Type, _core, this);
            group.SetNodes(BuildNodes(p.Name, p.All!, _proxies, p.Now), p.Now, SortIndex);
            Groups.Add(group);
        }

        HasData = Groups.Count > 0;
        StatusText = $"총 {Groups.Count}개 프록시 그룹";
    }

    private static List<ProxyItemViewModel> BuildNodes(
        string groupName,
        IReadOnlyList<string> members,
        IReadOnlyDictionary<string, ProxyItem> proxies,
        string? now)
    {
        var nodes = new List<ProxyItemViewModel>();
        foreach (var member in members)
        {
            if (!proxies.TryGetValue(member, out var p)) continue;
            nodes.Add(new ProxyItemViewModel(p.Name, p.Type, p.Alive, p.Delay, now == member, groupName: groupName));
        }
        return nodes;
    }

    public async Task SelectNodeAsync(string groupName, ProxyItemViewModel node)
    {
        var group = Groups.FirstOrDefault(g => g.Name == groupName);
        if (group is not null) group.SelectNode(node);
        await Task.CompletedTask;
    }

    /// <summary>모든 연결 닫기(노드 전환 후 오래된 연결 정리). 실패 시 예외 던지지 않음.</summary>
    public async Task CloseConnectionsSafeAsync()
    {
        if (_core.Api is null) return;
        try
        {
            await _core.Api.CloseAllConnectionsAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 한 노드의 측정 결과를 모든 그룹의 같은 이름 노드에 동기화(같은 물리 노드가 여러 그룹에 동시에 존재할 수 있음).
    /// 지연과 생존 여부만 갱신하고 IsTesting은 건드리지 않아 다른 그룹의 병렬 측정을 방해하지 않음.
    /// </summary>
    public void SyncNodeDelay(string name, int delay, bool alive)
    {
        foreach (var group in Groups)
        {
            foreach (var node in group.AllNodes)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                {
                    node.IsAlive = alive;
                    node.Delay = delay;
                }
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (_core.Api is null) return;
        IsBusy = true;
        try
        {
            // 노드 이름 기준 중복 제거: 같은 물리 노드는 여러 그룹에 있어도 한 번만 측정,
            // 중복 동시 요청끼리 경쟁해 결과가 어긋나는 것 방지.
            var byName = new Dictionary<string, List<ProxyItemViewModel>>(StringComparer.Ordinal);
            foreach (var group in Groups)
            {
                foreach (var node in group.AllNodes)
                {
                    if (!byName.TryGetValue(node.Name, out var list))
                    {
                        list = new List<ProxyItemViewModel>();
                        byName[node.Name] = list;
                    }
                    list.Add(node);
                }
            }

            var names = byName.Keys.ToList();
            foreach (var g in Groups) g.SetBusy(true);
            // 측정 중 대기 노드마다 회전 애니메이션을 켬(단일 노드 측정은 TestNodeAsync에서 이미 처리).
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var node in byName.Values.SelectMany(v => v))
                    node.IsTesting = true;
            });

            // DelayTester로 일원화: 동시성 제어 + 노드마다 여러 번 중 최솟값, 단일/그룹 측정과 동일.
            await DelayTester.MeasureAllAsync(
                _core.Api, names, DelayTestUrl, DelayTestTimeoutMs, DelayTestConcurrency,
                (name, delay) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        foreach (var node in byName[name])
                        {
                            node.IsTesting = false;
                            node.Delay = delay;
                            node.IsAlive = delay > 0;
                        }
                    });
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

            _dispatcher.TryEnqueue(() =>
            {
                foreach (var g in Groups) g.SetBusy(false);
                foreach (var g in Groups) if (g.SortIndex == 1) g.ApplySort();
                StatusText = $"측정 완료({names.Count}개 노드)";
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSortIndexChanged(int value)
    {
        foreach (var g in Groups) g.SortIndex = value;
        if (_loadingSort) return;
        _ = global::Resenz.App.AppHost.Host.Settings.SetIntAsync("ui.proxySort", value);
    }
}
