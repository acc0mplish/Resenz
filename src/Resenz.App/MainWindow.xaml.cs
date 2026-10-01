using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Resenz.App.Mini;
using Resenz.App.Pages;
using Resenz.App.ViewModels;
using Resenz.Core.Services;
using Forms = System.Windows.Forms;

namespace Resenz.App;

public sealed partial class MainWindow : Window
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _coreStatusItem;
    private Forms.ToolStripMenuItem? _startCoreItem;
    private Forms.ToolStripMenuItem? _stopCoreItem;
    private Forms.ToolStripMenuItem? _restartCoreItem;
    private Forms.ToolStripMenuItem? _modeRuleItem;
    private Forms.ToolStripMenuItem? _modeGlobalItem;
    private Forms.ToolStripMenuItem? _modeDirectItem;
    private Forms.ToolStripMenuItem? _systemProxyItem;
    private Forms.ToolStripMenuItem? _tunItem;
    private Forms.ToolStripMenuItem? _miniPanelItem;
    private MiniWindow? _miniWindow;
    private bool _forceExit;
    private bool _trayMenuWasOpened;
    private bool _modeBarLoading;
    private double _paneWidth = 200;
    private double _dragStartWidth;
    private Microsoft.UI.Xaml.DispatcherTimer? _sizeSaveTimer;

    private const double MinPaneWidth = 160;
    private const double MaxPaneWidth = 420;

    /// <summary>상태 점 표시:_coreStatusItem 이것으로 식별하고 렌더러는 이를 근거로 비활성 회색 처리를 피함.</summary>
    private const int StatusDotTag = -1;

    public ModeSelectorViewModel ModeSelector { get; }

    public Services.HotkeyManager Hotkeys { get; }

    public MainWindow()
    {
        ModeSelector = new ModeSelectorViewModel(AppHost.Host, DispatcherQueue);
        Hotkeys = new Services.HotkeyManager(AppHost.Host, this);

        InitializeComponent();
        Title = "Resenz";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }

        // 기본 창 크기(사용자 조정 후 영속화, 재시작 시 복원. 기록이 없으면 작은 기본값 사용).
        RestoreWindowSize();
        _sizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sizeSaveTimer.Tick += async (_, _) =>
        {
            _sizeSaveTimer.Stop();
            await SaveWindowSizeAsync();
        };
        SizeChanged += (_, _) =>
        {
            // 최대화/최소화 때 저장된 일반 크기를 덮어쓰지 않음.
            if (AppWindow.Presenter is OverlappedPresenter p
                && p.State == OverlappedPresenterState.Maximized)
                return;
            _sizeSaveTimer?.Stop();
            _sizeSaveTimer?.Start();
        };

        ApplyBackdrop();
        // 생성자에서 홈 로드 금지: 미니 패널 전용 / 조용한 트레이 시작 때는 페이지를 만들지 않고,
        // 메인 창를 실제로 처음 표시할 때(Activate/ShowAndActivate) 로드하여 상주 메모리를 낮춤.

        _ = InitializePaneWidthAsync();
        _ = InitializeNavigationStyleAsync();
        _ = InitializeBackdropStyleAsync();

        ModeSelector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModeSelectorViewModel.SelectedIndex))
                SyncModeBar();
        };
        _ = InitializeModeBarAsync();

        Activated += (_, _) =>
        {
            EnsureInitialPageLoaded();
            _ = ModeSelector.RefreshSwitchesAsync();
        };

        CreateTrayIcon();

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        AppHost.Host.Core.StateChanged += (_, _) => Dispatch(UpdateTrayState);
        AppHost.Host.Core.StateChanged += OnCoreStateForRestore;
        AppHost.Host.Core.StateChanged += OnCoreStateForAutoQuit;
        ViewModels.AppSignals.SwitchesChanged += (_, _) => Dispatch(UpdateTrayState);

        _ = Hotkeys.LoadAsync();
    }

    // ---- 단축키로 발동되는 동작(DispatcherQueue로 UI 스레드 복귀)----

    /// <summary>단축키: 시스템 프록시 전환.</summary>
    public void HotkeyToggleSystemProxy() => ToggleSystemProxy();

    /// <summary>단축키: TUN 모드 전환.</summary>
    public void HotkeyToggleTun()
    {
        try
        {
            var current = AppHost.Host.Settings.GetBoolAsync("core.tun").GetAwaiter().GetResult();
            // ModeSelector로 일괄 처리(설정 기록, 핫스위치, 브로드캐스트 동기화)하여 설정 중복 기록을 피함.
            _ = ModeSelector.SetTunAsync(!current);
        }
        catch
        {
        }
    }

    /// <summary>단축키: 실행 모드 전환(0 규칙 / 1 전역 / 2 직접).</summary>
    public async Task HotkeySelectModeAsync(int index)
    {
        try
        {
            await ModeSelector.SelectAsync(index);
            SyncModeBar();
        }
        catch
        {
        }
    }

    private CancellationTokenSource? _autoQuitCts;

    /// <summary>코어 정지 후 자동 종료가 켜져 있으면 카운트다운 뒤 앱 종료.</summary>
    private void OnCoreStateForAutoQuit(object? sender, CoreStateChanged e)
    {
        _autoQuitCts?.Cancel();
        _autoQuitCts = null;

        if (e.State is not (CoreState.Stopped or CoreState.Error)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var host = AppHost.Host;
                if (!await host.Settings.GetBoolAsync("ui.autoQuitWithoutCore").ConfigureAwait(false)) return;
                var delay = await host.Settings.GetIntAsync("ui.autoQuitWithoutCoreDelay", 30).ConfigureAwait(false);
                if (delay < 0) delay = 0;

                var cts = new CancellationTokenSource();
                _autoQuitCts = cts;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // 카운트다운 종료 때 코어가 아직 실행 중이 아니면 종료.
                if (host.Core.State is CoreState.Stopped or CoreState.Error)
                    Dispatch(ExitFromTray);
            }
            catch
            {
            }
        });
    }

    private bool _restoreInFlight;

    /// <summary>코어가 실행 상태에 들어오면 각 프록시 그룹에 대해 마지막에 수동 선택한 노드를 복원.</summary>
    private void OnCoreStateForRestore(object? sender, CoreStateChanged e)
    {
        if (e.State != CoreState.Running || _restoreInFlight) return;
        _restoreInFlight = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // 코어 API 준비 대기.
                await Task.Delay(800).ConfigureAwait(false);
                var host = AppHost.Host;
                var saved = await ProxySelectionStore.LoadAsync(host.Settings).ConfigureAwait(false);
                if (saved.Count == 0 || host.Core.Api is null) return;

                var proxies = await host.Core.Api.GetProxiesAsync().ConfigureAwait(false);
                var applied = 0;
                foreach (var (group, node) in saved)
                {
                    if (!proxies.TryGetValue(group, out var groupItem)) continue;
                    if (groupItem.All is null || !groupItem.All.Contains(node)) continue;
                    if (string.Equals(groupItem.Now, node, StringComparison.Ordinal)) continue;
                    try
                    {
                        await host.Core.Api.SelectProxyAsync(group, node).ConfigureAwait(false);
                        applied++;
                    }
                    catch
                    {
                    }
                }

                if (applied > 0)
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    AppSignals.RaiseProxiesChanged();
                }
            }
            catch
            {
            }
            finally
            {
                _restoreInFlight = false;
            }
        });
    }

    /// <summary>탐색 스타일 전환: 0 = 좌측, 1 = 상단.</summary>
    public void ApplyNavigationStyle(int style)
    {
        try
        {
            NavView.PaneDisplayMode = style == 1
                ? NavigationViewPaneDisplayMode.Top
                : NavigationViewPaneDisplayMode.Left;

            // 상단 탐색일 때 사이드바 드래그 바는 무의미하므로 숨김.
            _navigationOnTop = style == 1;
            PaneSplitter.Visibility = _navigationOnTop
                ? Visibility.Collapsed
                : (NavView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed);
        }
        catch
        {
        }
    }

    private bool _navigationOnTop;
    public void ApplyBackdrop()
    {
        try
        {
            if (DesktopAcrylicController.IsSupported())
                SetBackdrop(new Controls.AlwaysActiveAcrylicBackdrop());
            else if (MicaController.IsSupported())
                SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
        }
        catch
        {
        }
    }

    /// <summary>시스템 배경 소재를 교체하고 이전 인스턴스를 해제(AlwaysActiveAcrylicBackdrop가 보유한 컨트롤러는 명시적 해제 필요).</summary>
    private void SetBackdrop(SystemBackdrop? next)
    {
        var previous = SystemBackdrop;
        SystemBackdrop = next;
        if (!ReferenceEquals(previous, next) && previous is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
            }
        }
    }

    /// <summary>배경 소재: 0=아크릴(아래 창 비침), 1=Mica, 2=단색. 지원하지 않는 값은 폴백.</summary>
    public void ApplyBackdropStyle(int style)
    {
        try
        {
            switch (style)
            {
                case 1 when MicaController.IsSupported():
                    SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                    break;
                case 2:
                    SetBackdrop(null);
                    break;
                default:
                    if (DesktopAcrylicController.IsSupported())
                        SetBackdrop(new Controls.AlwaysActiveAcrylicBackdrop());
                    else if (MicaController.IsSupported())
                        SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                    else
                        SetBackdrop(null);
                    break;
            }
        }
        catch
        {
        }

        _miniWindow?.ApplyBackdropStyle(style);
    }

    /// <summary>
    /// Mica 배경과 제목 표시줄 버튼은 루트 요소 RequestedTheme의 영향을 받지 않아 별도로 테마를 따라가야 함.
    /// </summary>
    public void ApplyThemeToChrome(string theme)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var dark = theme == "dark" || (theme == "default" && IsSystemDark());
            var fg = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                          : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            titleBar.ButtonForegroundColor = fg;
            titleBar.ForegroundColor = fg;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch
        {
        }

        // 창/트레이 아이콘은 테마를 따름(라이트는 검은 고양이, 다크는 흰 고양이).
        UpdateTrayState();
    }

    private void CreateTrayIcon()
    {
        try
        {
            var initialFile = "TrayDefault.ico";
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", initialFile)),
                Text = "Resenz",
                Visible = true,
            };
            _lastTrayIconFile = initialFile;
            _notifyIcon.MouseDown += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    Dispatch(() => OnTrayLeftClick());
            };

            var menu = new Forms.ContextMenuStrip
            {
                ImageScalingSize = new Size(16, 16),
                ShowImageMargin = true,
            };
            menu.Opening += (_, _) =>
            {
                _trayMenuWasOpened = true;
                ApplyMenuTheme(menu);
                UpdateTrayState();
            };
            menu.Opened += (_, _) => TryRoundMenuWindow(menu);

            var (darkIcon, lightIcon) = GlyphColors();

            _coreStatusItem = new Forms.ToolStripMenuItem("코어: 알 수 없음") { Enabled = false, Tag = StatusDotTag };
            menu.Items.Add(_coreStatusItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(MakeIconItem("메인 화면 열기", 0xE80F, darkIcon, () => Dispatch(ShowAndActivate)));
            _miniPanelItem = MakeIconItem("미니 패널 표시", 0xE90B, darkIcon, () => Dispatch(ToggleMiniWindow));
            menu.Items.Add(_miniPanelItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            var coreMenu = MakeIconItem("코어", 0xE768, darkIcon, null);
            _startCoreItem = MakeIconItem("코어 시작", 0xE768, darkIcon, () => Dispatch(async () => await AppHost.Host.Core.StartAsync()));
            _stopCoreItem = MakeIconItem("코어 정지", 0xE71A, darkIcon, () => Dispatch(async () => await AppHost.Host.Core.StopAsync()));
            _restartCoreItem = MakeIconItem("코어 재시작", 0xE72C, darkIcon, () => Dispatch(async () => await AppHost.Host.Core.RestartAsync()));
            coreMenu.DropDownItems.AddRange([_startCoreItem, _stopCoreItem, _restartCoreItem]);
            menu.Items.Add(coreMenu);

            var modeMenu = MakeIconItem("프록시 모드", 0xE7C8, darkIcon, null);
            _modeRuleItem = MakeIconItem("규칙 모드", 0xE8A5, darkIcon, () => Dispatch(async () => await HotkeySelectModeAsync(0)));
            _modeGlobalItem = MakeIconItem("전역 모드", 0xE774, darkIcon, () => Dispatch(async () => await HotkeySelectModeAsync(1)));
            _modeDirectItem = MakeIconItem("직접 모드", 0xE711, darkIcon, () => Dispatch(async () => await HotkeySelectModeAsync(2)));
            foreach (var item in new[] { _modeRuleItem, _modeGlobalItem, _modeDirectItem })
                item.CheckOnClick = false;
            modeMenu.DropDownItems.AddRange([_modeRuleItem, _modeGlobalItem, _modeDirectItem]);
            menu.Items.Add(modeMenu);

            menu.Items.Add(new Forms.ToolStripSeparator());

            _systemProxyItem = MakeIconItem("시스템 프록시", 0xE774, darkIcon, () => Dispatch(ToggleSystemProxy));
            _systemProxyItem.CheckOnClick = false;
            menu.Items.Add(_systemProxyItem);

            _tunItem = MakeIconItem("TUN 모드", 0xE968, darkIcon, () => Dispatch(ToggleTun));
            _tunItem.CheckOnClick = false;
            menu.Items.Add(_tunItem);

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(MakeIconItem("종료", 0xE7E8, darkIcon, () => Dispatch(ExitFromTray)));

            _notifyIcon.ContextMenuStrip = menu;
            UpdateTrayState();
        }
        catch
        {
            _notifyIcon = null;
        }
    }

    /// <summary>아이콘 있는 메뉴 항목 생성(glyph 코드를 Tag에 기록해 테마 전환 시 재생성에 사용).</summary>
    private static Forms.ToolStripMenuItem MakeIconItem(string text, int code, Color color, Action? onClick)
        => new(text, IconGlyph(code, color), onClick is null ? null : (_, _) => onClick()) { Tag = code };

    /// <summary>현재 테마에 따라 다크/라이트 아이콘 색 반환.</summary>
    private static (Color Dark, Color Light) GlyphColors()
        => (Color.FromArgb(0xEC, 0xEC, 0xEC), Color.FromArgb(0x2B, 0x2B, 0x2B));

    /// <summary>Segoe Fluent Icons(폴백 Segoe MDL2 Assets)로 글리프를 20x20 비트맵으로 렌더링.</summary>
    private static Bitmap? IconGlyph(int code, Color color)
    {
        try
        {
            var glyph = Convert.ToChar(code);
            var family = "Segoe Fluent Icons";
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            if (!installed.Families.Any(f => f.Name == "Segoe Fluent Icons"))
                family = "Segoe MDL2 Assets";

            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font(family, 14f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            var rect = new RectangleF(0, 0, 16, 16);
            g.DrawString(glyph.ToString(), font, brush, rect, sf);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>16x16 단색 원 점을 렌더링해 코어 실행 상태 표시에 사용.</summary>
    private static Bitmap? StatusDot(Color color)
    {
        try
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 8, 8);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void Dispatch(Action action)
    {
        var queue = App.Main?.DispatcherQueue;
        if (queue is null) action();
        else queue.TryEnqueue(() => action());
    }

    private void Dispatch(Func<Task> action)
        => Dispatch(() => _ = action());

    private void UpdateTrayState()
    {
        if (_notifyIcon is null) return;
        try
        {
            var core = AppHost.Host.Core;
            var dark = IsAppDark();
            var running = core.State == CoreState.Running;
            if (_coreStatusItem is not null)
            {
                var statusText = core.State switch
                {
                    CoreState.Running => $"코어: 실행 중 {core.Version}",
                    CoreState.Starting => "코어: 시작 중",
                    CoreState.Stopping => "코어: 정지 중",
                    CoreState.Error => "코어: 이상",
                    _ => "코어: 정지됨",
                };
                _coreStatusItem.Text = statusText;
                var dotColor = core.State switch
                {
                    CoreState.Running => Color.FromArgb(0x3C, 0xB0, 0x4A),       // 초록: 실행 중
                    CoreState.Starting => Color.FromArgb(0xEA, 0xA5, 0x3B),      // 노랑: 시작 중
                    CoreState.Stopping => Color.FromArgb(0xF0, 0x7C, 0x00),      // 주황: 정지 중
                    CoreState.Error => Color.FromArgb(0xE5, 0x48, 0x4D),         // 빨강: 이상
                    _ => Color.FromArgb(0x9E, 0x9E, 0x9E),                        // 회색: 정지됨
                };
                var prev = _coreStatusItem.Image;
                _coreStatusItem.Image = StatusDot(dotColor);
                prev?.Dispose();
            }

            if (_startCoreItem is not null)
                _startCoreItem.Enabled = !running && core.State is not (CoreState.Starting or CoreState.Stopping);
            if (_stopCoreItem is not null)
                _stopCoreItem.Enabled = running || core.State is CoreState.Starting or CoreState.Stopping;
            if (_restartCoreItem is not null)
                _restartCoreItem.Enabled = running || core.State is CoreState.Starting or CoreState.Stopping;

            if (_modeRuleItem is not null)
                _modeRuleItem.Checked = ModeSelector.Mode == "rule";
            if (_modeGlobalItem is not null)
                _modeGlobalItem.Checked = ModeSelector.Mode == "global";
            if (_modeDirectItem is not null)
                _modeDirectItem.Checked = ModeSelector.Mode == "direct";

            if (_miniPanelItem is not null)
                _miniPanelItem.Text = _miniWindow?.IsVisible == true ? "미니 패널 숨기기" : "미니 패널 표시";

            var proxyOn = AppHost.Host.SystemProxy.IsEnabled();
            if (_systemProxyItem is not null)
                _systemProxyItem.Checked = proxyOn;

            var tunOn = false;
            try
            {
                tunOn = AppHost.Host.Settings.GetBoolAsync("core.tun").GetAwaiter().GetResult();
            }
            catch
            {
            }
            if (_tunItem is not null)
                _tunItem.Checked = tunOn;

            _notifyIcon.Text = core.State == CoreState.Running
                ? $"Resenz · 실행 중"
                : "Resenz · 정지됨";

            UpdateTrayIcon(core.State, proxyOn, tunOn);
        }
        catch
        {
        }
    }

    private string? _lastTrayIconFile;

    /// <summary>
    /// 트레이 아이콘 상태별 전환(타일 스타일):
    /// 시스템 프록시와 TUN 동시 켜짐=빨강. 시스템 프록시만 켠 경우=초록. 나머지(TUN만 켠 경우 포함)=기본.
    /// </summary>
    private void UpdateTrayIcon(CoreState state, bool proxyOn, bool tunOn)
    {
        if (_notifyIcon is null) return;
        try
        {
            var running = state == CoreState.Running;
            string file;
            if (running && proxyOn && tunOn) file = "TrayRed.ico";
            else if (running && proxyOn) file = "TrayGreen.ico";
            else file = "TrayDefault.ico";

            if (file == _lastTrayIconFile) return;

            var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
            if (!File.Exists(path)) return;

            var newIcon = new Icon(path);
            var old = _notifyIcon.Icon;
            _notifyIcon.Icon = newIcon;
            old?.Dispose();
            _lastTrayIconFile = file;

            // Windows 트레이는 낡은 아이콘을 캐시해 바로 다시 그리지 않음: 비운 뒤 Visible 재설정으로 강제 새로고침.
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
        }
        catch
        {
        }
    }

    private async void ToggleSystemProxy()
    {
        // ModeSelector로 일괄 처리: 프록시 기록, 메인/미니 패널 브로드캐스트 동기화, 빠른 연속 조작 수렴 지원.
        var host = AppHost.Host;
        try
        {
            var enabled = !host.SystemProxy.IsEnabled();
            await ModeSelector.SetSystemProxyAsync(enabled);
        }
        catch
        {
        }
        UpdateTrayState();
    }

    /// <summary>트레이 메뉴에서 TUN 모드 전환(ModeSelector의 전체 로직 재사용: 설정 기록, 설정 재생성, 코어 재시작).</summary>
    private async void ToggleTun()
    {
        try
        {
            var current = await AppHost.Host.Settings.GetBoolAsync("core.tun").ConfigureAwait(false);
            await ModeSelector.SetTunAsync(!current).ConfigureAwait(false);
        }
        catch
        {
        }
        Dispatch(UpdateTrayState);
    }

    private void ApplyMenuTheme(Forms.ContextMenuStrip menu)
    {
        var dark = IsAppDark();
        menu.Renderer = dark
            ? new FluentMenuRenderer(true, new DarkColorTable())
            : new FluentMenuRenderer(false, new Forms.ProfessionalColorTable());
        var textColor = dark ? Color.White : Color.Black;
        var iconColor = dark ? Color.FromArgb(0xEC, 0xEC, 0xEC) : Color.FromArgb(0x2B, 0x2B, 0x2B);
        ApplyItemsTheme(menu.Items, textColor, iconColor);
    }

    /// <summary>메뉴 항목 전경색과 세로 간격을 재귀적으로 설정하고 아이콘 색을 다시 만듦. Tag가 int면 glyph 코드로 다시 그림.</summary>
    private static void ApplyItemsTheme(Forms.ToolStripItemCollection items, Color textColor, Color iconColor)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            item.ForeColor = textColor;
            if (item is not Forms.ToolStripMenuItem mi) continue;
            mi.Margin = new Forms.Padding(0, 2, 0, 2);
            if (mi.Tag is int code && code > 0)
            {
                var old = mi.Image;
                mi.Image = IconGlyph(code, iconColor);
                old?.Dispose();
            }
            if (mi.HasDropDownItems)
                ApplyItemsTheme(mi.DropDownItems, textColor, iconColor);
        }
    }

    /// <summary>
    /// 현재 실제 적용 테마가 다크인지 판단.
    /// 런타임 테마 전환은 루트 요소의 RequestedTheme만 변경 가능(Application.RequestedTheme은 불변),
    /// 따라서 앱이 저장한 테마 설정을 먼저 읽고, 없으면 루트 요소를 확인한 뒤, 마지막으로 시스템 테마로 폴백.
    /// </summary>
    private static bool IsAppDark()
    {
        // App.CurrentTheme ApplyTheme 안에서 함께 갱신하는 것이 가장 안전.
        if (App.CurrentTheme == "dark") return true;
        if (App.CurrentTheme == "light") return false;

        if (App.Main?.Content is FrameworkElement root)
        {
            if (root.RequestedTheme == ElementTheme.Dark) return true;
            if (root.RequestedTheme == ElementTheme.Light) return false;
        }

        return IsSystemDark();
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x2B, 0x2B, 0x2B);
        private static readonly Color HoverBg = Color.FromArgb(0x41, 0x41, 0x41);
        private static readonly Color HoverBorder = Color.FromArgb(0x55, 0x55, 0x55);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ToolStripGradientBegin => Bg;
        public override Color ToolStripGradientMiddle => Bg;
        public override Color ToolStripGradientEnd => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => HoverBorder;
        public override Color MenuItemBorder => HoverBorder;
        public override Color MenuItemSelected => HoverBg;
        public override Color MenuItemSelectedGradientBegin => HoverBg;
        public override Color MenuItemSelectedGradientEnd => HoverBg;
        public override Color MenuItemPressedGradientBegin => HoverBg;
        public override Color MenuItemPressedGradientMiddle => HoverBg;
        public override Color MenuItemPressedGradientEnd => HoverBg;
        public override Color SeparatorDark => HoverBorder;
        public override Color SeparatorLight => Bg;
        public override Color ButtonSelectedHighlight => HoverBg;
        public override Color ButtonSelectedHighlightBorder => HoverBorder;
        public override Color ButtonSelectedBorder => HoverBorder;
        public override Color ToolStripBorder => HoverBorder;
    }

    /// <summary>
    /// 커스텀 메뉴 렌더러: 펼침 화살표 색은 다크/라이트 테마를 따르고 메뉴 항목 hover 배경은 둥근 모서리로 변경.
    /// 시스템 ToolStripProfessionalRenderer의 화살표는 고정 시스템 색을 사용해 다크 테마에서 불분명함.
    /// </summary>
    private sealed class FluentMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private readonly bool _dark;
        private readonly Color _hoverBg;
        private readonly Color _pressedBg;
        private readonly Color _arrowColor;

        public FluentMenuRenderer(bool dark, Forms.ProfessionalColorTable table)
            : base(table)
        {
            _dark = dark;
            _hoverBg = dark ? Color.FromArgb(0x41, 0x41, 0x41) : Color.FromArgb(0xE5, 0xE5, 0xE5);
            _pressedBg = dark ? Color.FromArgb(0x35, 0x35, 0x35) : Color.FromArgb(0xCC, 0xCC, 0xCC);
            _arrowColor = dark ? Color.FromArgb(0xEC, 0xEC, 0xEC) : Color.FromArgb(0x2B, 0x2B, 0x2B);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == true ? _arrowColor : Color.FromArgb(0x80, _arrowColor);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemImage(Forms.ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item?.Tag is int tag && tag == StatusDotTag && e.Image is not null)
            {
                e.Graphics.DrawImage(e.Image, e.ImageRectangle);
                return;
            }
            base.OnRenderItemImage(e);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;

            var bg = e.Item.Pressed ? _pressedBg : _hoverBg;
            e.Graphics.FillRectangle(new SolidBrush(bg), new Rectangle(Point.Empty, e.Item.Size));
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>원본 메뉴 창에 DWM 둥근 모서리 설정(Win11 적용, Win10 무시).</summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>메인 메뉴가 열린 뒤 둥근 모서리를 설정하고 하위 메뉴에 재귀 적용. 중복 방지로 한 번만 실행.</summary>
    private void TryRoundMenuWindow(Forms.ContextMenuStrip menu)
    {
        try
        {
            RoundWindow(menu.Handle);
            HookDropDownRounding(menu.Items);
        }
        catch
        {
        }
    }

    private static void HookDropDownRounding(Forms.ToolStripItemCollection items)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            if (item is not Forms.ToolStripMenuItem mi || !mi.HasDropDownItems) continue;
            mi.DropDownOpening += (_, _) =>
            {
                try
                {
                    if (mi.DropDown is not null) RoundWindow(mi.DropDown.Handle);
                    HookDropDownRounding(mi.DropDown?.Items ?? mi.DropDownItems);
                }
                catch
                {
                }
            };
        }
    }

    private static void RoundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceExit) return;

        var behavior = AppHost.Host.Settings.GetAsync("closeBehavior").GetAwaiter().GetResult() ?? "tray";
        if (behavior == "exit")
        {
            ExitFromTray();
            return;
        }

        args.Cancel = true;
        MinimizeToTray();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Hotkeys.Dispose();
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _miniWindow?.ForceClose();
        _miniWindow = null;
    }

    private void MinimizeToTray()
    {
        AppWindow.Hide();
        ViewModels.AppSignals.RaiseMainWindowVisibility(false);
        // 메인 UI 페이지의 시각 트리를 해제하고 작업 집합을 압축: 트레이/미니 전용 모드에서 상주 메모리가 크게 줄어듦.
        ReleasePageContent();
    }

    /// <summary>메인 창을 트레이로 숨김(조용한 시작용).</summary>
    public void HideToTray() => MinimizeToTray();

    /// <summary>시작 시 바로 트레이로 최소화(App이 설정에 따라 호출).</summary>
    public void StartMinimizedToTray() => MinimizeToTray();

    private void OnTrayLeftClick()
    {
        if (_trayMenuWasOpened)
        {
            _trayMenuWasOpened = false;
            return;
        }
        ToggleMiniWindow();
    }

    public void ToggleMiniWindow()
    {
        EnsureMiniWindow();
        _miniWindow!.ToggleVisible();
    }

    public void ShowMiniPanel()
    {
        EnsureMiniWindow();
        _miniWindow!.Show();
    }

    /// <summary>미니 창을 지연 생성하고 닫기 알림을 구독. 드래그 접기로 닫힌 뒤에는 참조를 비워 다음 호출 때 다시 만듦.</summary>
    private void EnsureMiniWindow()
    {
        if (_miniWindow is not null) return;
        var mini = new MiniWindow();
        mini.Dismissed += (_, _) => _miniWindow = null;
        _miniWindow = mini;
    }

    public void ApplyMiniPanelTheme(string theme) => _miniWindow?.ApplyTheme(theme);

    public void ShowAndActivate()
    {
        EnsureInitialPageLoaded();
        AppWindow.Show();
        ViewModels.AppSignals.RaiseMainWindowVisibility(true);
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private Type? _lastPageType;

    /// <summary>첫 표시 또는 트레이 복원 때 페이지 로드: 마지막 페이지 타입 복원 우선, 없으면 홈 사용.</summary>
    private void EnsureInitialPageLoaded()
    {
        if (NavFrame.Content is not null) return;
        _lastPageType ??= typeof(DashboardPage);
        NavFrame.Navigate(_lastPageType);
    }

    /// <summary>트레이로 숨길 때 현재 페이지 내용을 해제(페이지 타입은 보존)하고 작업 집합을 압축해 상주 메모리를 낮춤.</summary>
    private void ReleasePageContent()
    {
        try
        {
            if (NavFrame.Content is not null)
                _lastPageType = NavFrame.Content.GetType();
            NavFrame.Content = null;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            TrimWorkingSet();
        }
        catch (Exception ex)
        {
            global::Resenz.App.App.WriteLog($"페이지 내용 해제 실패: {{ex}");
        }
    }

    /// <summary>물리 메모리 작업 집합을 시스템에 돌려줌(프라이빗 커밋은 줄지 않지만 작업 관리자 상 표시치가 눈에 띄게 내려감).</summary>
    private static void TrimWorkingSet()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
        }
        catch
        {
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private void ExitFromTray()
    {
        _forceExit = true;
        _miniWindow?.ForceClose();
        _miniWindow = null;
        _notifyIcon?.Dispose();
        _notifyIcon = null;

        try
        {
            // 먼저 시스템 프록시를 끄고 코어를 정지(TUN 어댑터와 라우팅 제거)한 뒤 남은 프로세스를 정리해 종료 후 호스트 단방을 방지.
            AppHost.Host.ShutdownNetwork();
            AppHost.Host.Process.KillOrphansAsync(AppHost.Host.Core.Paths.BinaryPath)
                .GetAwaiter().GetResult();
            AppHost.Host.Elevated.KillOrphanHosts();
        }
        catch
        {
        }

        AppHost.Shutdown();
        Close();
    }

    /// <summary>
    /// 앱 자가 업데이트 종료: 코어/TUN/시스템 프록시 정지 후 조용한 업데이트 스크립트 기동(이 프로세스 종료 대기 후
    /// 설치/덮어쓰기 후 --startcore로 재시작), 이후 메인 프로그램 종료.
    /// </summary>
    public void ExitForUpdate()
    {
        var script = AppHost.Host.AppUpdate.ConsumePendingScript();
        if (string.IsNullOrEmpty(script) || !File.Exists(script))
        {
            ExitFromTray();
            return;
        }

        _forceExit = true;

        try
        {
            // 먼저 cmd로 업데이트 스크립트를 숨김 창 방식으로 띄운 뒤 코어를 정지하고 종료해 이후를 스크립트에 맡김.
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            ExitFromTray();
            return;
        }

        try
        {
            // 시스템 프록시 정지, 코어 정지(TUN 어댑터와 라우팅 제거), 남은 프로세스 정리 후 즉시 종료.
            AppHost.Host.ShutdownNetwork();
            AppHost.Host.Process.KillOrphansAsync(AppHost.Host.Core.Paths.BinaryPath)
                .GetAwaiter().GetResult();
            AppHost.Host.Elevated.KillOrphanHosts();
        }
        catch
        {
        }

        AppHost.Shutdown();
        Close();
    }

    private void SyncModeBar()
    {
        _modeBarLoading = true;
        try
        {
            var index = ModeSelector.SelectedIndex;
            ModeRuleButton.IsChecked = index == 0;
            ModeGlobalButton.IsChecked = index == 1;
            ModeDirectButton.IsChecked = index == 2;
        }
        finally
        {
            _modeBarLoading = false;
        }
    }

    private const double DefaultWindowWidth = 1080;
    private const double DefaultWindowHeight = 700;

    /// <summary>마지막 창 크기 복원. 기록이 없으면 작은 기본 크기 사용.</summary>
    private void RestoreWindowSize()
    {
        try
        {
            var w = AppHost.Host.Settings.GetIntAsync("ui.windowWidth", 0).GetAwaiter().GetResult();
            var h = AppHost.Host.Settings.GetIntAsync("ui.windowHeight", 0).GetAwaiter().GetResult();
            if (w <= 0 || h <= 0)
            {
                w = (int)DefaultWindowWidth;
                h = (int)DefaultWindowHeight;
            }
            w = Math.Max(w, 900);
            h = Math.Max(h, 600);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        }
        catch
        {
        }
    }

    /// <summary>창 크기 영속화(디바운스 후 설정 기록).</summary>
    private async Task SaveWindowSizeAsync()
    {
        try
        {
            var size = AppWindow.Size;
            await AppHost.Host.Settings.SetIntAsync("ui.windowWidth", size.Width);
            await AppHost.Host.Settings.SetIntAsync("ui.windowHeight", size.Height);
        }
        catch
        {
        }
    }

    private async Task InitializePaneWidthAsync()
    {
        try
        {
            var saved = await AppHost.Host.Settings.GetIntAsync("ui.paneWidth", 200);
            _paneWidth = Math.Clamp(saved, MinPaneWidth, MaxPaneWidth);
        }
        catch
        {
            _paneWidth = 200;
        }
        ApplyPaneWidth();
    }

    private async Task InitializeNavigationStyleAsync()
    {
        int style;
        try
        {
            style = await AppHost.Host.Settings.GetIntAsync("ui.navigationStyle", 0);
        }
        catch
        {
            style = 0;
        }
        ApplyNavigationStyle(style);
    }

    private async Task InitializeBackdropStyleAsync()
    {
        int style;
        try
        {
            style = await AppHost.Host.Settings.GetIntAsync("ui.backdropStyle", 0);
        }
        catch
        {
            style = 0;
        }
        ApplyBackdropStyle(style);
    }

    private void ApplyPaneWidth()
    {
        NavView.OpenPaneLength = _paneWidth;
        PaneSplitter.Margin = new Thickness(_paneWidth, 0, 0, 0);
        PaneSplitter.Visibility = !_navigationOnTop && NavView.IsPaneOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PaneSplitter_DragStarted(object? sender, EventArgs e)
    {
        _dragStartWidth = _paneWidth;
    }

    private void PaneSplitter_Dragged(object? sender, double deltaX)
    {
        _paneWidth = Math.Clamp(_dragStartWidth + deltaX, MinPaneWidth, MaxPaneWidth);
        ApplyPaneWidth();
    }

    private void PaneSplitter_DragCompleted(object? sender, EventArgs e)
    {
        _ = AppHost.Host.Settings.SetIntAsync("ui.paneWidth", (int)Math.Round(_paneWidth));
    }

    private void NavView_PaneChanged(NavigationView sender, object args) => ApplyPaneWidth();

    private async Task InitializeModeBarAsync()
    {
        await ModeSelector.LoadAsync();
        SyncModeBar();
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modeBarLoading) return;
        if (sender is not ToggleButton button) return;
        if (button.Tag is not string tag || !int.TryParse(tag, out var index)) return;

        if (ModeSelector.SelectedIndex == index)
        {
            SyncModeBar();
            return;
        }

        await ModeSelector.SelectAsync(index);
        SyncModeBar();
    }

    private void SystemProxy_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ModeSelector.SystemProxyOn) return;
        _ = ModeSelector.SetSystemProxyAsync(toggle.IsOn);
    }

    private void Tun_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ModeSelector.TunOn) return;
        _ = ModeSelector.SetTunAsync(toggle.IsOn);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item) return;
        switch (item.Tag)
        {
            case "dashboard": NavFrame.Navigate(typeof(DashboardPage)); break;
            case "proxies": NavFrame.Navigate(typeof(ProxiesPage)); break;
            case "connections": NavFrame.Navigate(typeof(ConnectionsPage)); break;
            case "rules": NavFrame.Navigate(typeof(RulesPage)); break;
            case "profiles": NavFrame.Navigate(typeof(ProfilesPage)); break;
            case "traffic": NavFrame.Navigate(typeof(TrafficPage)); break;
            case "network": NavFrame.Navigate(typeof(NetworkPage)); break;
            case "logs": NavFrame.Navigate(typeof(LogsPage)); break;
        }
    }

    public void NavigateToTag(string tag)
    {
        // DispatcherQueue로 실행을 늦춰 NavigationView 로드가 끝났음을 보장.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.Equals(tag, "settings", StringComparison.OrdinalIgnoreCase))
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag is string value && value == tag)
                {
                    NavView.SelectedItem = item;
                    return;
                }
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
