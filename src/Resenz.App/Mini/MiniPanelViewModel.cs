using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Resenz.Core.Models;
using Resenz.Core.Services;
using Resenz.App.ViewModels;

namespace Resenz.App.Mini;

public sealed partial class MiniPanelViewModel : ObservableObject, IDisposable
{
    private readonly ResenzHost _host;
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private readonly EventHandler<TrafficSnapshot> _onTraffic;
    private readonly EventHandler<CoreStateChanged> _onState;
    private readonly EventHandler _onProxiesChanged;
    private bool _disposed;
    private IReadOnlyDictionary<string, ProxyItem> _proxies =
        new Dictionary<string, ProxyItem>();
    private bool _groupsLoaded;

    public ObservableCollection<MiniProxyGroupViewModel> Groups { get; } = new();

    public ModeSelectorViewModel ModeSelector { get; }

    private string _delayTestUrl = DelayTestSettings.DefaultUrl;
    private int _delayTestTimeoutMs = DelayTestSettings.DefaultTimeoutMs;
    private int _delayTestConcurrency = DelayTestSettings.DefaultConcurrency;
    private bool _autoCloseConnection = true;

    private Task<bool> IsAutoCloseConnectionAsync() => Task.FromResult(_autoCloseConnection);

    [ObservableProperty]
    public partial string UpRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string DownRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string CoreStateText { get; set; } = "정지됨";

    [ObservableProperty]
    public partial string StateKey { get; set; } = "stopped";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasGroups { get; set; }

    public DispatcherQueue Dispatcher => _dispatcher;

    public MiniPanelViewModel(ResenzHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _core = host.Core;
        _dispatcher = dispatcher;
        ModeSelector = new ModeSelectorViewModel(host, dispatcher);

        _onTraffic = (_, t) => _dispatcher.TryEnqueue(() =>
        {
            UpRate = Format.Rate(t.Up);
            DownRate = Format.Rate(t.Down);
        });
        _onState = (_, e) => _dispatcher.TryEnqueue(() =>
        {
            ApplyCoreState(e.State);
            if (e.State != CoreState.Running) _groupsLoaded = false;
            _ = ReloadAsync();
        });
        _onProxiesChanged = (_, _) =>
        {
            // 스스로 보낸 브로드캐스트(노드 전환 등)는 제자리 갱신되었으므로 전체 페이지 재구축 생략.
            if (_suppressSelfReload) return;
            _dispatcher.TryEnqueue(() => _ = LoadAsync(force: true));
        };

        _core.TrafficUpdated += _onTraffic;
        _core.StateChanged += _onState;
        AppSignals.ProxiesChanged += _onProxiesChanged;

        ApplyCoreState(_core.State);
    }

    /// <summary>모든 이벤트 구독 해제. 미니 창 닫을 때 호출하여 정적 이벤트에 구독이 쌓여 생기는 누수 방지.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.TrafficUpdated -= _onTraffic;
        _core.StateChanged -= _onState;
        AppSignals.ProxiesChanged -= _onProxiesChanged;
        ModeSelector.Dispose();
    }

    private bool _suppressSelfReload;

    private void ApplyCoreState(CoreState state)
    {
        IsRunning = state == CoreState.Running;
        CoreStateText = state switch
        {
            CoreState.Running => "실행 중",
            CoreState.Starting => "시작 중",
            CoreState.Stopping => "정지 중",
            CoreState.Error => "이상",
            _ => "정지됨",
        };
        StateKey = state switch
        {
            CoreState.Running => "running",
            CoreState.Starting => "starting",
            CoreState.Stopping => "stopping",
            CoreState.Error => "error",
            _ => "stopped",
        };
    }

    public async Task LoadModeAsync() => await ModeSelector.LoadAsync().ConfigureAwait(false);

    public async Task RefreshOnShowAsync()
    {
        await ModeSelector.LoadAsync().ConfigureAwait(false);
        await ModeSelector.RefreshSwitchesAsync().ConfigureAwait(false);
        var (url, timeout, concurrency) = await DelayTestSettings.ReadAsync(_host.Settings).ConfigureAwait(false);
        _delayTestUrl = url;
        _delayTestTimeoutMs = timeout;
        _delayTestConcurrency = concurrency;
        _autoCloseConnection = await _host.Settings.GetBoolAsync("core.autoCloseConnection", true).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    public async Task SelectModeAsync(int index) => await ModeSelector.SelectAsync(index);

    public async Task SetSystemProxyAsync(bool enabled) => await ModeSelector.SetSystemProxyAsync(enabled);

    public async Task SetTunAsync(bool enabled) => await ModeSelector.SetTunAsync(enabled);

    private async Task ReloadAsync()
    {
        await Task.Delay(400).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_core.Api is null || _core.State != CoreState.Running)
        {
            _groupsLoaded = false;
            _dispatcher.TryEnqueue(() =>
            {
                if (Groups.Count > 0)
                {
                    Groups.Clear();
                    HasGroups = false;
                }
            });
            return;
        }

        if (_groupsLoaded && !force) return;

        try
        {
            var proxies = await _core.Api.GetProxiesAsync().ConfigureAwait(false);
            _proxies = proxies;

            _dispatcher.TryEnqueue(() =>
            {
                Rebuild();
                _groupsLoaded = true;
            });
        }
        catch
        {
        }
    }

    private void Rebuild()
    {
        var declared = MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declared.Count; i++) rank[declared[i]] = i;

        var ordered = _proxies.Values
            .Where(p => p.All is { Count: > 0 })
            .Where(p => !string.Equals(p.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => rank.TryGetValue(p.Name, out var r) ? r : int.MaxValue)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups) seen.Add(group.Name);

        var wanted = ordered.Select(p => p.Name).ToList();
        var stale = Groups.Where(g => !wanted.Contains(g.Name, StringComparer.Ordinal)).ToList();
        foreach (var g in stale) Groups.Remove(g);

        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var group = Groups.FirstOrDefault(g => string.Equals(g.Name, p.Name, StringComparison.Ordinal));
            if (group is null)
            {
                group = new MiniProxyGroupViewModel(p.Name);
                Groups.Insert(Math.Min(i, Groups.Count), group);
            }
            group.Fill(p.All!, _proxies, p.Now);
        }

        HasGroups = Groups.Count > 0;
    }

    public async Task SelectNodeAsync(MiniProxyGroupViewModel group, MiniProxyNodeViewModel node)
    {
        if (_core.Api is null) return;
        try
        {
            await _core.Api.SelectProxyAsync(group.Name, node.Name).ConfigureAwait(false);
            if (await IsAutoCloseConnectionAsync().ConfigureAwait(false))
            {
                try
                {
                    await _core.Api.CloseAllConnectionsAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
            await ProxySelectionStore.RememberAsync(_host.Settings, group.Name, node.Name).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                // 이 페이지는 선택 상태를 제자리 갱신했으므로 브로드캐스트 때 자기 자신은 새로고침을 건너뛰어 전체 페이지 재구축 방지.
                _suppressSelfReload = true;
                try
                {
                    AppSignals.RaiseProxiesChanged();
                }
                finally
                {
                    _suppressSelfReload = false;
                }
            });
        }
        catch
        {
        }
    }

    public async Task TestGroupAsync(MiniProxyGroupViewModel group)
    {
        if (_core.Api is null || group.IsTesting) return;
        _dispatcher.TryEnqueue(() => group.IsTesting = true);
        // DelayTester로 일원화: 동시성 제어 + 노드마다 여러 번 중 최솟값, 메인 패널 측정 로직과 동일.
        var names = group.Nodes.Select(n => n.Name).ToList();
        var delays = new Dictionary<string, int>(StringComparer.Ordinal);
        await DelayTester.MeasureAllAsync(
            _core.Api, names, _delayTestUrl, _delayTestTimeoutMs, _delayTestConcurrency,
            (name, delay) =>
            {
                lock (delays) delays[name] = delay;
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        _dispatcher.TryEnqueue(() =>
        {
            group.ApplyDelays(delays);
            group.IsTesting = false;
            // 브로드캐스트로 메인 패널 등 다른 뷰가 지연을 동기화. 자신은 제자리 갱신되어 리로드 생략.
            _suppressSelfReload = true;
            try
            {
                AppSignals.RaiseProxiesChanged();
            }
            finally
            {
                _suppressSelfReload = false;
            }
        });
    }

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        if (IsRunning) await _core.StopAsync();
        else await _core.StartAsync();
    }

    [ObservableProperty]
    public partial bool IsUpdatingSubscription { get; set; }

    /// <summary>현재 구독을 새로고침하고 설정을 다시 적용. 미니 패널의 구독 업데이트 버튼에서 호출.</summary>
    [RelayCommand]
    private async Task UpdateSubscriptionAsync()
    {
        if (IsUpdatingSubscription) return;
        IsUpdatingSubscription = true;
        try
        {
            var active = await _host.Profiles.GetActiveAsync().ConfigureAwait(false);
            if (active is null || active.Kind != "url") return;

            await _host.Profiles.RefreshAsync(active.Id).ConfigureAwait(false);
            await _host.ApplyActiveProfileAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _dispatcher.TryEnqueue(() =>
            {
                IsUpdatingSubscription = false;
                _ = LoadAsync(force: true);
            });
        }
    }
}
