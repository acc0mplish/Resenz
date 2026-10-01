using Microsoft.UI.Xaml;
using Resenz.Core.Services;

namespace Resenz.App;

public partial class App : Application
{
    private Window? _window;

    public static MainWindow? Main { get; private set; }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Resenz", "app-crash.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteLog($"UnhandledException: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteLog($"AppDomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => WriteLog($"UnobservedTask: {e.Exception}");
    }

    public static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void ApplyTheme(string theme)
    {
        // Application.RequestedTheme 창 생성 전에만 설정 가능하며 실행 중 대입은 예외를 던지므로 반드시 보호해야 함.
        try
        {
            Current.RequestedTheme = theme switch
            {
                "dark" => ApplicationTheme.Dark,
                "light" => ApplicationTheme.Light,
                _ => Current.RequestedTheme,
            };
        }
        catch
        {
        }

        CurrentTheme = theme;

        // 런타임 테마 전환은 루트 요소의 RequestedTheme에 의존하며 하위 모든 컨트롤로 전파됨.
        if (Main?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "dark" => ElementTheme.Dark,
                "light" => ElementTheme.Light,
                _ => ElementTheme.Default,
            };
        }

        Main?.ApplyThemeToChrome(theme);
        Main?.ApplyMiniPanelTheme(theme);
    }

    public static string CurrentTheme { get; private set; } = "default";

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await AppHost.InitializeAsync();
        }
        catch (Exception ex)
        {
            WriteLog($"ResenzHost 초기화 실패: {{ex}");
        }

        _window = new MainWindow();
        Main = (MainWindow)_window;

        var launchArgs = Environment.GetCommandLineArgs();
        var launchedByStartup = launchArgs.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        var launchedAsMini = launchArgs.Any(a => string.Equals(a, "--mini", StringComparison.OrdinalIgnoreCase));

        // 미니 패널 전용 모드: 메인 창을 띄우지 않고 미니 패널만 표시해 메인 창 시각 트리 전체의 표시 비용을 절약.
        if (launchedAsMini)
        {
            _ = ShowMiniOnlyAsync();
            _ = ApplyThemeFromSettingsAsync();
            _ = AutoStartCoreAsync();
            _ = AutoCheckUpdateAsync();
            return;
        }

        // 조용한 시작(부팅 자동 시작 + 이 토글이 켜진 경우) 메인 창을 띄우지 않고 트레이로 최소화.
        var silent = false;
        try
        {
            if (launchedByStartup)
                silent = await AppHost.Host.Settings.GetBoolAsync("ui.silentStart").ConfigureAwait(false);
        }
        catch
        {
        }

        // '시작 시 트레이로 최소화': 실행할 때마다 메인 창을 띄우지 않고 트레이와 미니 패널만 유지.
        var startMinimized = false;
        try
        {
            startMinimized = await AppHost.Host.Settings.GetBoolAsync("ui.startMinimized").ConfigureAwait(false);
        }
        catch
        {
        }

        if (silent || startMinimized)
        {
            var main = Main;
            if (main is not null)
                main.DispatcherQueue.TryEnqueue(() => main.StartMinimizedToTray());
        }
        else
        {
            _window.Activate();
        }

        _ = ApplyThemeFromSettingsAsync();
        _ = AutoStartCoreAsync();
        _ = NavigateFromCommandLineAsync();
        _ = AutoCheckUpdateAsync();
    }

    /// <summary>미니 패널 전용 모드: 준비 대기를 마친 뒤 미니 패널만 띄우고 메인 창은 활성화하지 않음.</summary>
    private static async Task ShowMiniOnlyAsync()
    {
        try
        {
            await Task.Delay(800).ConfigureAwait(false);
            var main = Main;
            if (main is not null)
                main.DispatcherQueue.TryEnqueue(() => main.ShowMiniPanel());
        }
        catch (Exception ex)
        {
            WriteLog($"미니 패널 표시 실패: {{ex}");
        }
    }

    /// <summary>시작 시 백그라운드에서 조용히 앱 업데이트 검사(방해 없음, 결과는 설정 페이지 표시용).</summary>
    private static async Task AutoCheckUpdateAsync()
    {
        try
        {
            var enabled = await AppHost.Host.Settings.GetBoolAsync("ui.autoCheckAppUpdate", true).ConfigureAwait(false);
            if (!enabled) return;
            // 코어 시작 이후로 미루어 시작 트래픽과 대역폭 경쟁 회피.
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            _ = AppHost.Host.AppUpdate.CheckAsync();
        }
        catch (Exception ex)
        {
            WriteLog($"업데이트 자동 검사 실패: {{ex}");
        }
    }

    private static async Task NavigateFromCommandLineAsync()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            string? tag = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--page", StringComparison.OrdinalIgnoreCase))
                {
                    tag = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrEmpty(tag)) return;

            // 창과 NavigationView 로드 완료 대기.
            await Task.Delay(600);
            Main?.NavigateToTag(tag);
        }
        catch (Exception ex)
        {
            WriteLog($"--page 매개변수 처리 실패: {{ex}");
        }
    }

    private static async Task ApplyThemeFromSettingsAsync()
    {
        try
        {
            var theme = await AppHost.Host.Settings.GetAsync("theme").ConfigureAwait(false) ?? "default";
            ApplyTheme(theme);
        }
        catch (Exception ex)
        {
            WriteLog($"테마 적용 실패: {{ex}");
        }
    }

    private static async Task AutoStartCoreAsync()
    {
        try
        {
            var host = AppHost.Host;
            // 업데이트 후 --startcore 플래그로 재시작: 설정과 무관하게 코어를 강제로 띄워 업데이트 전 상태를 복원.
            var fromUpdate = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--startcore", StringComparison.OrdinalIgnoreCase));
            var autoStart = fromUpdate
                || await host.Settings.GetBoolAsync("core.autoStart", true).ConfigureAwait(false);
            if (!autoStart) return;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            // 시작 시 구독 자동 업데이트가 켜져 있으면 현재 구독을 먼저 새로고침 후 적용(현재 구독은 즉시 사용 가능해야 함).
            try
            {
                var autoUpdate = await host.Settings.GetBoolAsync("profile.autoUpdate", true).ConfigureAwait(false);
                if (autoUpdate)
                {
                    var active = await host.Profiles.GetActiveAsync().ConfigureAwait(false);
                    if (active is { Kind: "url" })
                        await host.Profiles.RefreshAsync(active.Id).ConfigureAwait(false);

                    // 나머지 구독은 백그라운드에서 시간차 새로고침(각 수 초 간격)하여 시작 때 네트워크 쟁탈 방지.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var items = await host.Profiles.ListAsync().ConfigureAwait(false);
                            var others = items.Where(i => i.Kind == "url" && i.Id != active?.Id).ToList();
                            for (var i = 0; i < others.Count; i++)
                            {
                                await host.Profiles.RefreshAsync(others[i].Id).ConfigureAwait(false);
                                await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                        }
                    });
                }
            }
            catch
            {
            }

            // 현재 구독을 적용해 runtime 설정을 만든 뒤 코어 기동.
            await host.ApplyActiveProfileAsync().ConfigureAwait(false);
            await host.Core.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteLog($"코어 자동 기동 실패: {{ex}");
        }
    }
}
