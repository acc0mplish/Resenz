using Resenz.Data;

namespace Resenz.Core.Services;

public sealed class ResenzHost : IDisposable
{
    public ResenzDatabase Database { get; }
    public ISettingsService Settings { get; }
    public IMihomoProcessManager Process { get; }
    public ICoreManager Core { get; }
    public ISystemProxyService SystemProxy { get; }
    public IStartupService Startup { get; }
    public TrafficRepository Traffic { get; }
    public IKernelUpdateService KernelUpdate { get; }
    public IProfileService Profiles { get; }
    public IElevatedClient Elevated { get; }
    public IAppUpdateService AppUpdate { get; }
    public SsidService Ssid { get; }
    public PacServer Pac { get; } = new();

    private readonly TrafficRecorder _recorder;
    private readonly Timer _profileRefreshTimer;
    private readonly string _root;

    public static string ResolveRootDirectory()
    {
        var dir = ResenzAppData.ResolveDirectory();
        Directory.CreateDirectory(dir);
        return dir;
    }

    public ResenzHost(string? rootDirectory = null)
    {
        _root = rootDirectory ?? ResolveRootDirectory();
        Directory.CreateDirectory(_root);

        Database = new ResenzDatabase(Path.Combine(_root, "resenz.db"));
        Settings = new SettingsService(Database);
        Process = new MihomoProcessManager();
        SystemProxy = new SystemProxyService();
        Startup = new StartupService();
        Traffic = new TrafficRepository(Database);

        var coreDir = Path.Combine(_root, "core");
        Directory.CreateDirectory(coreDir);

        KernelUpdate = new KernelUpdateService(coreDir, Settings);
        Elevated = new ElevatedClient(Path.Combine(AppContext.BaseDirectory, "Resenz.Elevated.exe"));
        Profiles = new ProfileService(Database, Settings, Path.Combine(_root, "profiles"));
        AppUpdate = new AppUpdateService(Settings);
        Ssid = new SsidService(this);

        var paths = new CorePaths(
            BinaryPath: KernelUpdate.BinaryPath,
            WorkingDirectory: coreDir,
            RuntimeConfigPath: Path.Combine(coreDir, "runtime.yaml"),
            ControllerAddress: "http://127.0.0.1:9090",
            ControllerPort: 9090,
            Secret: "resenz",
            ElevatedHostPath: Path.Combine(AppContext.BaseDirectory, "Resenz.Elevated.exe"));

        Core = new CoreManager(Process, Settings, Elevated, SystemProxy, paths);
        _recorder = new TrafficRecorder(Traffic);
        Core.TrafficUpdated += (_, snap) => _recorder.Record(snap);
        Core.ConnectionsUpdated += (_, snap) => _recorder.RecordConnections(snap);

        _profileRefreshTimer = new Timer(OnProfileRefresh, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await Database.InitializeAsync().ConfigureAwait(false);
        // 첫 실행: 무료 공개 구독 3개를 미리 등록(프로필이 이미 있으면 건너뜀). 실패해도 시작은 계속.
        try
        {
            await Profiles.SeedFreeSubscriptionsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
        }
        // 내장판: 첫 실행 시 설치 디렉터리의 bundled에서 코어/wintun/geodata를 해제(이미 있으면 건너뜀).
        try
        {
            var bundle = Path.Combine(AppContext.BaseDirectory, "bundled");
            if (Directory.Exists(bundle)) KernelUpdate.ProvisionFromBundle(bundle);
        }
        catch
        {
        }
        await Core.InitializeAsync(ct).ConfigureAwait(false);
        // 수동 다운로드 프록시 포트를 불러옴(코어 미실행 시 코어/geodata 다운로드용).
        DownloadProxy.SetManualPort(await Settings.GetIntAsync("core.downloadProxyPort", 0).ConfigureAwait(false));
        // SSID 인지: WiFi에 따라 구독 자동 전환 / 프록시 일시중지.
        Ssid.Start();
    }

    private void OnProfileRefresh(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var enabled = await Settings.GetBoolAsync("profile.autoUpdate", true).ConfigureAwait(false);
                if (!enabled) return;
                var items = await Profiles.ListAsync().ConfigureAwait(false);
                foreach (var item in items.Where(i => i.Kind == "url"))
                    await Profiles.RefreshAsync(item.Id).ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    /// <summary>
    /// 설정만 기준으로 runtime.yaml 재작성(코어 reload 트리거 안 함). TUN 등 설정 핫스위치 시 사용
    /// 먼저 파일을 기록한 뒤 PATCH로 코어에 즉시 적용해 전체 리로드를 피함.
    /// </summary>
    public async Task RewriteRuntimeConfigAsync(CancellationToken ct = default)
    {
        var active = await Profiles.GetActiveAsync(ct).ConfigureAwait(false);
        if (active is null) return;
        var options = await BuildRuntimeOptionsAsync(ct).ConfigureAwait(false);
        var yaml = await Profiles.GenerateRuntimeConfigAsync(active.Id, options, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Core.Paths.RuntimeConfigPath, yaml, ct).ConfigureAwait(false);
    }

    public async Task<bool> ApplyActiveProfileAsync(CancellationToken ct = default)
    {
        var active = await Profiles.GetActiveAsync(ct).ConfigureAwait(false);
        if (active is null) return false;

        var options = await BuildRuntimeOptionsAsync(ct).ConfigureAwait(false);
        var yaml = await Profiles.GenerateRuntimeConfigAsync(active.Id, options, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Core.Paths.RuntimeConfigPath, yaml, ct).ConfigureAwait(false);

        var reloaded = true;
        if (Core.State == CoreState.Running)
            reloaded = await Core.ReloadConfigAsync(Core.Paths.RuntimeConfigPath, ct).ConfigureAwait(false);

        // 설정 리로드 후 프록시 그룹/노드가 바뀌었음을 알려 각 뷰가 다시 가져오게 함.
        ProxiesReloaded?.Invoke(this, EventArgs.Empty);
        return reloaded;
    }

    /// <summary>구독 새로고침이나 설정 전환으로 runtime 설정이 리로드된 뒤 발생하며, 각 뷰가 프록시 데이터를 강제로 다시 가져오는 데 사용.</summary>
    public event EventHandler? ProxiesReloaded;

    public async Task<RuntimeYamlOptions> BuildRuntimeOptionsAsync(CancellationToken ct = default)
    {
        var dnsHijack = await GetStringListAsync("core.tunDnsHijack").ConfigureAwait(false);
        var exclude = await GetStringListAsync("core.tunRouteExcludeAddress").ConfigureAwait(false);

        return new RuntimeYamlOptions(
            MixedPort: await Settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false),
            ControllerPort: Core.Paths.ControllerPort,
            Secret: Core.Paths.Secret,
            Mode: await Settings.GetAsync("core.mode").ConfigureAwait(false) ?? "rule",
            LogLevel: await Settings.GetAsync("core.logLevel").ConfigureAwait(false) ?? "info",
            AllowLan: await Settings.GetBoolAsync("core.allowLan").ConfigureAwait(false),
            Ipv6: await Settings.GetBoolAsync("core.ipv6", true).ConfigureAwait(false),
            TunEnabled: await Settings.GetBoolAsync("core.tun").ConfigureAwait(false),
            TunStack: await Settings.GetAsync("core.tunStack").ConfigureAwait(false) ?? "mixed",
            TunAutoRoute: await Settings.GetBoolAsync("core.tunAutoRoute", true).ConfigureAwait(false),
            TunAutoRedirect: await Settings.GetBoolAsync("core.tunAutoRedirect").ConfigureAwait(false),
            TunAutoDetectInterface: await Settings.GetBoolAsync("core.tunAutoDetectInterface", true).ConfigureAwait(false),
            TunStrictRoute: await Settings.GetBoolAsync("core.tunStrictRoute").ConfigureAwait(false),
            TunMtu: await Settings.GetIntAsync("core.tunMtu", 0).ConfigureAwait(false),
            TunDnsHijack: dnsHijack,
            TunRouteExcludeAddress: exclude);
    }

    /// <summary>줄바꿈으로 구분된 다중 값 설정 항목을 읽어 빈 값·중복 제거 목록 반환.</summary>
    private async Task<IReadOnlyList<string>?> GetStringListAsync(string key)
    {
        var raw = await Settings.GetAsync(key).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var items = raw
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        return items.Count > 0 ? items : null;
    }

    /// <summary>현재 프로세스가 관리자 권한으로 실행 중인지(TUN 필요).</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {        _profileRefreshTimer.Dispose();
        Ssid.Dispose();
        Pac.Dispose();
        Core.Dispose();
        _recorder.Dispose();
    }

    /// <summary>
    /// 종료 전에 코어를 정지하고 호스트 인터넷을 막는 흔적을 정리. 시스템 프록시 해제는 Core.StopAsync에 응집되어 있으므로
    /// 여기서는 코어가 확실히 멈추는 것만 보장하면 됨(코어 종료 시 TUN 어댑터와 라우팅은 자동 제거).
    /// </summary>
    public void ShutdownNetwork()
    {
        try
        {
            Core.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
        try
        {
            Pac.Stop();
        }
        catch
        {
        }
    }
}
