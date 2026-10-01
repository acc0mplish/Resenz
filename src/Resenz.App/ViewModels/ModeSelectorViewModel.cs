using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

public sealed partial class ModeSelectorViewModel : ObservableObject, IDisposable
{
    private static readonly string[] Modes = ["rule", "global", "direct"];

    private const string ProxyBypass = "localhost;127.*;10.*;172.16.*;192.168.*";

    private readonly ResenzHost _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly EventHandler<CoreStateChanged> _onCoreState;
    private readonly EventHandler _onSwitchesChanged;
    private bool _disposed;
    private bool _loading;
    private bool _suppressSwitch;
    private bool _switchBusy;
    private bool? _pendingTun;
    private bool _proxyBusy;
    private bool? _pendingProxy;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string ModeText { get; set; } = "규칙";

    [ObservableProperty]
    public partial bool SystemProxyOn { get; set; }

    [ObservableProperty]
    public partial bool TunOn { get; set; }

    public string Mode { get; private set; } = "rule";

    public bool IsGlobal => Mode == "global";

    public event EventHandler<string>? ModeChanged;

    public ModeSelectorViewModel(ResenzHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _onCoreState = (_, e) => _dispatcher.TryEnqueue(() => OnCoreStateChanged(e.State));
        _onSwitchesChanged = (_, _) => _dispatcher.TryEnqueue(() => _ = LoadAsync());
        _host.Core.StateChanged += _onCoreState;
        AppSignals.SwitchesChanged += _onSwitchesChanged;
        OnCoreStateChanged(_host.Core.State);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Core.StateChanged -= _onCoreState;
        AppSignals.SwitchesChanged -= _onSwitchesChanged;
    }

    private void OnCoreStateChanged(CoreState state)
    {
        IsRunning = state == CoreState.Running;
        _ = SyncSwitchesAsync();
        if (IsRunning) _ = ReloadAsync();
    }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var mode = await _host.Settings.GetAsync("core.mode") ?? "rule";

            if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
            {
                try
                {
                    var config = await _host.Core.Api.GetConfigsAsync();
                    if (config is not null) mode = config.Mode;
                }
                catch
                {
                }
            }

            var proxyOn = _host.SystemProxy.IsEnabled();
            var tunOn = await _host.Settings.GetBoolAsync("core.tun").ConfigureAwait(false);

            _dispatcher.TryEnqueue(() =>
            {
                ApplyMode(mode);
                _suppressSwitch = true;
                try
                {
                    SystemProxyOn = proxyOn;
                    TunOn = tunOn;
                }
                finally
                {
                    _suppressSwitch = false;
                }
            });
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ReloadAsync()
    {
        await Task.Delay(500);
        await LoadAsync();
    }

    private void ApplyMode(string mode)
    {
        var index = Array.IndexOf(Modes, mode);
        if (index < 0) index = 0;
        SelectedIndex = index;
        ModeText = index switch
        {
            1 => "전역",
            2 => "직접 연결",
            _ => "규칙",
        };

        var normalized = Modes[index];
        if (normalized != Mode)
        {
            Mode = normalized;
            OnPropertyChanged(nameof(IsGlobal));
            ModeState.Set(normalized);
            ModeChanged?.Invoke(this, normalized);
        }
    }

    public async Task SelectAsync(int index)
    {
        if (_loading || index < 0 || index >= Modes.Length) return;

        var mode = Modes[index];
        await _host.Settings.SetAsync("core.mode", mode);

        if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
        {
            try
            {
                await _host.Core.Api.PatchConfigsAsync(new { mode });
            }
            catch
            {
            }
        }

        ApplyMode(mode);
        AppSignals.RaiseSwitchesChanged();
    }

    private async Task SyncSwitchesAsync()
    {
        var proxyOn = _host.SystemProxy.IsEnabled();
        var tunOn = await _host.Settings.GetBoolAsync("core.tun").ConfigureAwait(false);
        _dispatcher.TryEnqueue(() =>
        {
            _suppressSwitch = true;
            try
            {
                SystemProxyOn = proxyOn;
                TunOn = tunOn;
            }
            finally
            {
                _suppressSwitch = false;
            }
        });
    }

    public async Task RefreshSwitchesAsync() => await SyncSwitchesAsync().ConfigureAwait(false);

    public async Task SetSystemProxyAsync(bool enabled)
    {
        if (_suppressSwitch) return;

        if (_proxyBusy)
        {
            _pendingProxy = enabled;
            return;
        }
        _proxyBusy = true;
        _pendingProxy = null;
        try
        {
            var port = await _host.Settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false);
            var mode = await _host.Settings.GetAsync("ui.systemProxyMode").ConfigureAwait(false) ?? "manual";

            if (enabled)
            {
                var ok = ApplySystemProxy(mode, port);
                // 단절 등 순간 실패: 5초 뒤 한 번 재시도.
                if (!ok)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    ApplySystemProxy(mode, port);
                }
            }
            else
            {
                _host.SystemProxy.Disable();
            }
        }
        catch
        {
        }
        finally
        {
            _proxyBusy = false;
            await SyncSwitchesAsync().ConfigureAwait(false);
            AppSignals.RaiseSwitchesChanged();

            if (_pendingProxy is not null)
            {
                var next = _pendingProxy.Value;
                _pendingProxy = null;
                await SetSystemProxyAsync(next).ConfigureAwait(false);
            }
        }
    }

    private bool ApplySystemProxy(string mode, int port)
    {
        try
        {
            if (mode == "pac")
            {
                if (_host.Pac.Start(port))
                    return _host.SystemProxy.EnablePac(_host.Pac.PacUrl ?? $"http://127.0.0.1:{port}/proxy.pac");
                return _host.SystemProxy.Enable($"127.0.0.1:{port}", ProxyBypass);
            }
            return _host.SystemProxy.Enable($"127.0.0.1:{port}", ProxyBypass);
        }
        catch
        {
            return false;
        }
    }

    public async Task SetTunAsync(bool enabled)
    {
        if (_suppressSwitch) return;

        // 빠른 연속 전환 수렴: 전환 중 최신 요청을 기록하고 완료 후 마지막 요청으로 이어가 연타 시 상태 유실을 방지.
        if (_switchBusy)
        {
            _pendingTun = enabled;
            return;
        }
        _switchBusy = true;
        _pendingTun = null;
        try
        {
            await Task.Run(async () =>
            {
                await _host.Settings.SetBoolAsync("core.tun", enabled).ConfigureAwait(false);

                if (enabled)
                {
                    try
                    {
                        await _host.KernelUpdate.EnsureWintunAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                // 먼저 설정을 runtime.yaml에 기록(TUN 섹션은 core.tun 기준으로 재생성)하여 다음 전체 리로드/재시작에 사용.
                await _host.RewriteRuntimeConfigAsync().ConfigureAwait(false);

                if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
                {
                    // 핫스위치: tun.enable만 PATCH하여 코어가 즉시 적용. 프로세스 재시작 없음, 시스템 프록시 건드리지 않음.
                    try
                    {
                        await _host.Core.Api.PatchConfigsAsync(new Dictionary<string, object>
                        {
                            ["tun"] = new Dictionary<string, object>
                            {
                                ["enable"] = enabled,
                            },
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                        // PATCH 실패(오래된 코어는 핫스위치 미지원) 시 폴백: 설정 전체 리로드로 tun 섹션 적용,
                        // 그래도 실패할 때 코어 재시작.
                        var reloaded = await _host.ApplyActiveProfileAsync().ConfigureAwait(false);
                        if (!reloaded)
                            await _host.Core.RestartAsync().ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _switchBusy = false;
            await SyncSwitchesAsync().ConfigureAwait(false);
            AppSignals.RaiseSwitchesChanged();

            // 전환 중 새 요청이 들어오면 바로 이어서 실행해 최종 상태가 사용자의 마지막 조작과 같도록 보장.
            if (_pendingTun is not null)
            {
                var next = _pendingTun.Value;
                _pendingTun = null;
                await SetTunAsync(next).ConfigureAwait(false);
            }
        }
    }
}
