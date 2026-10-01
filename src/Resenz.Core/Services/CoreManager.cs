using Resenz.Core.Models;

namespace Resenz.Core.Services;

public enum CoreState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

public sealed record CoreStateChanged(CoreState State, string? Version, string? Error);

public sealed record CorePaths(
    string BinaryPath,
    string WorkingDirectory,
    string RuntimeConfigPath,
    string ControllerAddress,
    int ControllerPort,
    string Secret,
    string ElevatedHostPath);

public interface ICoreManager : IDisposable
{
    CoreState State { get; }
    string? Version { get; }
    string? LastError { get; }
    CorePaths Paths { get; }
    IMihomoApiClient? Api { get; }
    bool IsElevatedMode { get; }

    event EventHandler<CoreStateChanged>? StateChanged;
    event EventHandler<TrafficSnapshot>? TrafficUpdated;
    event EventHandler<MemorySnapshot>? MemoryUpdated;
    event EventHandler<ConnectionsSnapshot>? ConnectionsUpdated;
    event EventHandler<LogEntry>? LogReceived;

    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default);
    Task<bool> RestartAsync(CancellationToken ct = default);
    Task<bool> ValidateAsync(string configPath, CancellationToken ct = default);
}

public sealed class CoreManager : ICoreManager
{
    private readonly IMihomoProcessManager _process;
    private readonly ISettingsService _settings;
    private readonly IElevatedClient _elevated;
    private readonly ISystemProxyService _systemProxy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _streamGate = new();
    private readonly Timer _watchdog;

    private IMihomoApiClient? _api;
    private CancellationTokenSource? _streamCts;
    private CoreState _state = CoreState.Stopped;
    private string? _version;
    private string? _lastError;
    private bool _tunRequested;
    private bool _restoreSystemProxyAfterStart;

    public CorePaths Paths { get; }
    public CoreState State => _state;
    public string? Version => _version;
    public string? LastError => _lastError;
    public IMihomoApiClient? Api => _api;
    public bool IsElevatedMode { get; private set; }

    public event EventHandler<CoreStateChanged>? StateChanged;
    public event EventHandler<TrafficSnapshot>? TrafficUpdated;
    public event EventHandler<MemorySnapshot>? MemoryUpdated;
    public event EventHandler<ConnectionsSnapshot>? ConnectionsUpdated;
    public event EventHandler<LogEntry>? LogReceived;

    public CoreManager(IMihomoProcessManager process, ISettingsService settings, IElevatedClient elevated, ISystemProxyService systemProxy, CorePaths paths)
    {
        _process = process;
        _settings = settings;
        _elevated = elevated;
        _systemProxy = systemProxy;
        Paths = paths;
        _process.Exited += OnProcessExited;
        _watchdog = new Timer(OnWatchdog, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Resenz", "core.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Paths.WorkingDirectory);
        if (!File.Exists(Paths.RuntimeConfigPath))
        {
            var options = new RuntimeYamlOptions(
                MixedPort: 7890,
                ControllerPort: Paths.ControllerPort,
                Secret: Paths.Secret,
                Mode: "rule",
                LogLevel: "info",
                AllowLan: false,
                Ipv6: true,
                TunEnabled: false);
            await File.WriteAllTextAsync(Paths.RuntimeConfigPath, MihomoConfigBuilder.BuildFallbackYaml(options), ct)
                .ConfigureAwait(false);
        }
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is CoreState.Running or CoreState.Starting) return _state == CoreState.Running;

            SetState(CoreState.Starting, null, null);

            if (!File.Exists(Paths.BinaryPath))
            {
                SetState(CoreState.Error, null, $"코어 미발견: {{Paths.BinaryPath}");
                return false;
            }

            // 지난 비정상 종료가 남긴 이 앱 코어 프로세스 정리.
            await _process.KillOrphansAsync(Paths.BinaryPath, ct).ConfigureAwait(false);

            _tunRequested = await _settings.GetBoolAsync("core.tun").ConfigureAwait(false);

            var options = new CoreLaunchOptions(
                Paths.BinaryPath,
                Paths.WorkingDirectory,
                Paths.RuntimeConfigPath,
                Paths.Secret);

            var started = false;
            if (_tunRequested)
            {
                if (ResenzHost.IsRunningAsAdmin())
                {
                    // 메인 프로그램이 이미 관리자: 코어를 자식 프로세스로 직접 기동해 관리자 토큰 상속, TUN 적용.
                    started = await _process.StartAsync(options, ct).ConfigureAwait(false);
                    IsElevatedMode = false;
                }
                else
                {
                    // 관리자로 실행되지 않음: 상승 호스트를 거쳐 기동 시도(구 경로 호환).
                    started = await StartElevatedAsync(ct).ConfigureAwait(false);
                    IsElevatedMode = started;
                    if (!started)
                        Log($"[CoreManager] 상승 기동 실패, 일반 모드로 폴백(TUN 적용 안 됨):{_lastError}");
                }
            }

            if (!started)
            {
                started = await _process.StartAsync(options, ct).ConfigureAwait(false);
                IsElevatedMode = false;
            }

            if (!started)
            {
                SetState(CoreState.Error, null, "코어 프로세스 기동 실패");
                return false;
            }

            _api = new MihomoApiClient(Paths.ControllerAddress, Paths.Secret);

            var version = await WaitForReadyAsync(ct).ConfigureAwait(false);
            if (version is null)
            {
                SetState(CoreState.Error, null, "코어 시작 후 API 연결 불가");
                await StopAsync(ct).ConfigureAwait(false);
                return false;
            }

            _version = version;
            // 'TUN 요청했으나 관리자 권한 없음' 경우에만 경고. 메인 프로그램 자체가 관리자면 TUN이 이미 적용되어 경고하지 않음.
            var tunIneffective = _tunRequested && !IsElevatedMode && !ResenzHost.IsRunningAsAdmin();
            var portWarning = tunIneffective
                ? $"TUN 모드 시작 실패, 일반 프록시로 폴백함(TUN 미적용). 원인: {{_lastError}"
                : await CheckPortConflictAsync(ct).ConfigureAwait(false);
            SetState(CoreState.Running, version, portWarning);
            RestoreOwnSystemProxy();
            StartStreams();
            // 코어 준비 완료. 이후 다운로드(코어/geodata/구독)는 로컬 혼합 포트로, 자체 프록시를 활용.
            DownloadProxy.SetCorePort(await _settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false));
            return true;
        }
        catch (Exception ex)
        {
            SetState(CoreState.Error, null, ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartElevatedAsync(CancellationToken ct)
    {
        if (!File.Exists(Paths.ElevatedHostPath))
        {
            _lastError = $"상승 호스트 미발견: {{Paths.ElevatedHostPath}";
            return false;
        }

        if (!_elevated.IsElevatedHostRunning)
        {
            if (!_elevated.LaunchElevatedHost())
            {
                _lastError = _elevated.LaunchError
                    ?? "상승 호스트 기동 실패(UAC 승인 거부 가능)";
                return false;
            }
            await Task.Delay(1200, ct).ConfigureAwait(false);
        }

        var response = await _elevated
            .StartCoreAsync(Paths.BinaryPath, Paths.WorkingDirectory, Paths.RuntimeConfigPath, Paths.Secret, ct)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            _lastError = response.Message;
            return false;
        }

        return true;
    }

    private async Task<string?> CheckPortConflictAsync(CancellationToken ct)
    {
        if (_api is null) return null;
        try
        {
            var config = await _api.GetConfigsAsync(ct).ConfigureAwait(false);
            if (config is null) return null;
            if (config.MixedPort == 0)
                return "혼합 포트 바인딩 실패. 다른 프로그램이 점유 중일 수 있음. 설정에서 다른 포트로 바꿔 주세요.";
        }
        catch
        {
        }
        return null;
    }

    private async Task<string?> WaitForReadyAsync(CancellationToken ct)
    {
        if (_api is null) return null;
        // 첫 실행 시 코어가 MMDB/GeoSite 등 지리 데이터를 내려받아야 해 시간이 오래 걸릴 수 있으므로 대기 시간을 넉넉히 줌.
        for (var i = 0; i < 240; i++)
        {
            if (ct.IsCancellationRequested) return null;
            var version = await _api.GetVersionAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(version)) return version;
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    private void StartStreams()
    {
        lock (_streamGate)
        {
            StopStreamsLocked();
            _streamCts = new CancellationTokenSource();
            var ct = _streamCts.Token;
            var api = _api;
            if (api is null) return;

            _ = RunStreamAsync(() => api.StartTrafficStreamAsync(s => { TrafficUpdated?.Invoke(this, s); return Task.CompletedTask; }, ct), ct);
            _ = RunStreamAsync(() => api.StartMemoryStreamAsync(s => { MemoryUpdated?.Invoke(this, s); return Task.CompletedTask; }, ct), ct);
            _ = RunStreamAsync(() => PollConnectionsAsync(api, ct), ct);
            _ = RunStreamAsync(() => api.StartLogStreamAsync(s => { LogReceived?.Invoke(this, s); return Task.CompletedTask; }, ct), ct);
        }
    }

    private async Task PollConnectionsAsync(IMihomoApiClient api, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var snapshot = await api.GetConnectionsAsync(ct).ConfigureAwait(false);
            if (snapshot is not null) ConnectionsUpdated?.Invoke(this, snapshot);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task RunStreamAsync(Func<Task> stream, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await stream().ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }

            if (ct.IsCancellationRequested) return;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
        }
    }

    private void StopStreamsLocked()
    {
        if (_streamCts is null) return;
        try
        {
            _streamCts.Cancel();
            _streamCts.Dispose();
        }
        catch
        {
        }
        _streamCts = null;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == CoreState.Stopped) return;
            SetState(CoreState.Stopping, _version, null);

            // 코어가 멈추면 로컬 혼합 포트를 가리키는 시스템 프록시 때문에 호스트가 끊기므로 함께 끔.
            // 원래 상태를 기록해 이후 StartAsync(재시작 시나리오)에서 복원.
            _restoreSystemProxyAfterStart = TryDisableOwnSystemProxy();

            lock (_streamGate) StopStreamsLocked();

            _api?.Dispose();
            _api = null;

            if (IsElevatedMode)
            {
                try
                {
                    await _elevated.StopCoreAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                }
                IsElevatedMode = false;
            }
            else
            {
                await _process.StopAsync(ct).ConfigureAwait(false);
            }

            _version = null;
            SetState(CoreState.Stopped, null, null);
            // 코어 정지됨. 다운로드는 직접 연결로 되돌림(프록시 포트 더 이상 사용 불가).
            DownloadProxy.SetCorePort(0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 시스템 프록시가 로컬 혼합 포트를 가리키면 끄고 실제로 껐는지 반환(재시작 후 복원용).
    /// 이 앱이 설정한 프록시만 끔. 사용자가 수동으로 설정한 프록시를 잘못 끄지 않게 함.
    /// </summary>
    private bool TryDisableOwnSystemProxy()
    {
        try
        {
            var port = _settings.GetIntAsync("core.mixedPort", 7890).GetAwaiter().GetResult();
            var expected = $"127.0.0.1:{port}";
            if (_systemProxy.IsEnabled()
                && string.Equals(_systemProxy.CurrentServer(), expected, StringComparison.OrdinalIgnoreCase))
            {
                _systemProxy.Disable();
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private void RestoreOwnSystemProxy()
    {
        if (!_restoreSystemProxyAfterStart) return;
        _restoreSystemProxyAfterStart = false;
        try
        {
            var port = _settings.GetIntAsync("core.mixedPort", 7890).GetAwaiter().GetResult();
            _systemProxy.Enable($"127.0.0.1:{port}", "localhost;127.*;10.*;172.16.*;192.168.*");
        }
        catch
        {
        }
    }

    public async Task<bool> ValidateAsync(string configPath, CancellationToken ct = default)
    {
        var options = new CoreLaunchOptions(
            Paths.BinaryPath,
            Paths.WorkingDirectory,
            configPath,
            Paths.Secret);
        return await _process.ValidateConfigAsync(options, ct).ConfigureAwait(false);
    }

    public async Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default)
    {
        if (_api is null || _state != CoreState.Running) return false;
        try
        {
            await _api.ReloadConfigAsync(configPath, force: true, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);
        return await StartAsync(ct).ConfigureAwait(false);
    }

    private void OnProcessExited(object? sender, CoreProcessExitedEventArgs e)
    {
        lock (_streamGate) StopStreamsLocked();
        if (_state is CoreState.Stopping or CoreState.Stopped) return;
        SetState(CoreState.Error, _version,
            string.IsNullOrWhiteSpace(e.LastError)
                ? $"코어 프로세스 종료됨(코드 {e.ExitCode})"
                : $"코어 프로세스 종료됨(코드 {e.ExitCode}): {e.LastError}");
    }

    private void OnWatchdog(object? state)
    {
        if (!IsElevatedMode || _state != CoreState.Running) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var status = await _elevated.GetStatusAsync().ConfigureAwait(false);
                if (status.Success) return;
                SetState(CoreState.Error, _version, $"상승 코어 종료됨: {{status.Message}");
                lock (_streamGate) StopStreamsLocked();
                IsElevatedMode = false;
            }
            catch
            {
            }
        });
    }

    private void SetState(CoreState state, string? version, string? error)
    {
        _state = state;
        if (version is not null) _version = version;
        _lastError = error;
        StateChanged?.Invoke(this, new CoreStateChanged(state, _version, error));
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _process.Exited -= OnProcessExited;
        lock (_streamGate) StopStreamsLocked();
        _api?.Dispose();
        _process.Dispose();

        // 상승 모드 여부와 무관하게 상승 호스트가 살아 있으면 종료시켜야 함. 그러지 않으면 실행 파일을 잠가버림.
        if (_elevated.IsElevatedHostRunning)
        {
            try
            {
                _elevated.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }
}

