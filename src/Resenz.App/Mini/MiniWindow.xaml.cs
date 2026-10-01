using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Resenz.App.Mini;

public sealed partial class MiniWindow : Window
{
    private const int DesignWidth = 380;
    private const int DesignHeight = 540;
    private const int MarginRight = 12;
    private const int MarginBottom = 12;
    private const int ShowDurationMs = 260;
    private const int HideDurationMs = 220;

    private enum VisState
    {
        Hidden,
        Showing,
        Shown,
        Hiding,
    }

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly WindowSlider _slider;
    private readonly DispatcherTimer _statsTimer;
    private bool _forceClose;
    private VisState _state = VisState.Hidden;
    private Windows.Graphics.RectInt32 _targetRect;

    public MiniWindow()
    {
        InitializeComponent();

        Title = "Resenz";
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _slider = new WindowSlider(_hwnd, DispatcherQueue);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        WindowChrome.SetToolWindow(_hwnd);
        WindowChrome.SetRoundCorner(_hwnd);
        WindowChrome.RemoveNonClientFrame(_hwnd);
        WindowChrome.SetTopmost(_hwnd);

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom);
        _appWindow.MoveAndResize(_targetRect);

        _appWindow.Closing += OnClosing;

        Panel.OpenFullRequested += (_, _) => OpenFullPanel();
        Panel.CollapseRequested += (_, _) => Hide();
        Panel.DragGestureStarted += (_, _) => OnDragStarted();
        Panel.DragGestureMoved += (_, _) => OnDragMoved();
        Panel.DragGestureEnded += (_, _) => OnDragEnded();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statsTimer.Tick += async (_, _) => await Panel.ViewModel.LoadAsync();

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        ApplyBackdropStyle(await ReadBackdropStyleAsync());
        await Panel.InitializeAsync();
    }

    private static async Task<int> ReadBackdropStyleAsync()
    {
        try
        {
            return await global::Resenz.App.AppHost.Host.Settings.GetIntAsync("ui.backdropStyle", 0);
        }
        catch
        {
            return 0;
        }
    }

    public void ApplyTheme(string theme)
    {
        Panel.RequestedTheme = theme switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>배경 소재: 0=아크릴(아래 창 비침), 1=Mica,2=단색. 메인 창과 일치 유지.</summary>
    public void ApplyBackdropStyle(int style)
    {
        try
        {
            switch (style)
            {
                case 1 when MicaController.IsSupported():
                    SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                    Panel.SetSolidBackground(false);
                    break;
                case 2:
                    SetBackdrop(null);
                    Panel.SetSolidBackground(true);
                    break;
                default:
                    if (global::Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
                    {
                        SetBackdrop(new global::Resenz.App.Controls.AlwaysActiveAcrylicBackdrop());
                        Panel.SetSolidBackground(false);
                    }
                    else if (MicaController.IsSupported())
                    {
                        SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                        Panel.SetSolidBackground(false);
                    }
                    else
                    {
                        SetBackdrop(null);
                        Panel.SetSolidBackground(true);
                    }
                    break;
            }
        }
        catch
        {
        }
    }

    /// <summary>배경 소재를 교체하고 이전 인스턴스를 해제해 소재 전환 시 컨트롤러 누수 방지.</summary>
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

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose) return;
        args.Cancel = true;
        Hide();
    }

    public void ForceClose()
    {
        _forceClose = true;
        _statsTimer.Stop();
        Panel.ViewModel.Dispose();
        _slider.Dispose();
        Close();
    }

    public bool IsVisible => _state is VisState.Showing or VisState.Shown;

    public void ToggleVisible()
    {
        if (_state is VisState.Showing or VisState.Shown) Hide();
        else Show();
    }

    public void Show()
    {
        bool wasHidden = _state is VisState.Hidden;
        _state = VisState.Showing;

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom);
        var hiddenRect = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        Windows.Graphics.RectInt32 start = hiddenRect;
        if (!wasHidden)
        {
            var current = DpiLayout.GetCurrentRect(_hwnd);
            if (current.Width > 0 && current.Height > 0) start = current;
        }
        else
        {
            _appWindow.MoveAndResize(start);
        }

        _appWindow.Show();
        WindowChrome.BringToForeground(_hwnd);
        // 반드시 마지막에 작업 표시줄 아래로 삽입: 작업 표시줄이 창을 가려야 슬라이드할 때
        // 창이 작업 표시줄 뒤로 올라오는 듯한 느낌을 줌.
        WindowChrome.PlaceBelowTaskbar(_hwnd);

        // 등장: fast-out / slow-in, 끝에 천천히 감속. Fluent 펼치기 곡선에 맞춤.
        _slider.Animate(start, _targetRect, 255, 255, ShowDurationMs, SlideEasing.EaseOut, OnShowCompleted);

        _statsTimer.Start();
        _ = Panel.RefreshOnShowAsync();
    }

    private void OnShowCompleted()
    {
        if (_state != VisState.Showing) return;
        _state = VisState.Shown;
    }

    public void Hide()
    {
        if (_state is VisState.Hidden or VisState.Hiding) return;
        _state = VisState.Hiding;

        _statsTimer.Stop();

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom);
        var hiddenRect = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        var current = DpiLayout.GetCurrentRect(_hwnd);
        if (current.Width <= 0 || current.Height <= 0) current = _targetRect;

        // 접기 전에 창을 작업 표시줄 아래로 다시 삽입: 아래로 내릴 때 작업 표시줄에 가려져
        // 작업 표시줄 뒤로 숨어드는 느낌을 줌(드래그 접기와 동일).
        WindowChrome.PlaceBelowTaskbar(_hwnd);

        // 접기: 역방향 곡선, 시작이 천천히 떠오름. Fluent 종료 애니메이션에 맞춤.
        _slider.Animate(current, hiddenRect, 255, 255, HideDurationMs, SlideEasing.EaseIn, OnHideCompleted);
    }

    private void OnHideCompleted()
    {
        if (_state != VisState.Hiding) return;
        _appWindow.Hide();
        _appWindow.MoveAndResize(_targetRect);
        _state = VisState.Hidden;
    }

    private void OpenFullPanel()
    {
        Hide();
        global::Resenz.App.App.Main?.ShowAndActivate();
    }

    // ---- 아래로 끌어 닫기 제스처: 빈 곳을 누른 채 아래로 끌면 손을 따라 이동. 임계값 초과 시 내려가며 닫고, 아니면 복귀 ----

    private const double DragDismissThresholdRatio = 0.28;

    private bool _dragging;
    private Windows.Graphics.RectInt32 _dragStartRect;
    private int _dragStartCursorY;

    private void OnDragStarted()
    {
        if (_state != VisState.Shown) return;
        _slider.Cancel();
        // 드래그 내내 작업 표시줄이 창을 덮게 함: topmost 그룹 내 작업 표시줄 바로 아래에 삽입해 작업 표시줄 위로 덮지 않게 함.
        WindowChrome.PlaceBelowTaskbar(_hwnd);
        _dragging = true;
        _dragStartRect = DpiLayout.GetCurrentRect(_hwnd);
        GetCursorPos(out var p);
        _dragStartCursorY = p.Y;
    }

    private void OnDragMoved()
    {
        if (!_dragging) return;
        GetCursorPos(out var p);
        int dy = p.Y - _dragStartCursorY;
        // 따라오기: 창이 마우스를 따라 움직임. 시작 위치 아래로 제한해 화면 위로 끌어올려 나가지 않게 함(원위치로 되돌리면 취소).
        int y = Math.Max(_dragStartRect.Y, _dragStartRect.Y + dy);
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            _dragStartRect.X, y, _dragStartRect.Width, _dragStartRect.Height));
    }

    private void OnDragEnded()
    {
        if (!_dragging) return;
        _dragging = false;
        GetCursorPos(out var p);
        int dy = p.Y - _dragStartCursorY;
        var current = DpiLayout.GetCurrentRect(_hwnd);

        if (dy >= _dragStartRect.Height * DragDismissThresholdRatio)
            DismissFromDrag(current);
        else
            SnapBackFromDrag(current);
    }

    /// <summary>드래그 접기(아래로 끝까지 내린 뒤 실제 닫기), 호스트에 참조 비우기 알림.</summary>
    private void DismissFromDrag(Windows.Graphics.RectInt32 current)
    {
        _statsTimer.Stop();
        // 아래로 내리기 전에 창을 작업 표시줄 아래 층에 삽입해 자연스러운 접기와 동일하게: 작업 표시줄에 가려지며 아래에서 숨어듦.
        WindowChrome.PlaceBelowTaskbar(_hwnd);
        var hidden = SlideMath.Hidden(current, SlideDirection.BottomUp);
        _slider.Animate(current, hidden, 255, 255, 240, SlideEasing.EaseIn, () =>
        {
            _state = VisState.Hidden;
            _appWindow.Hide();
            _appWindow.MoveAndResize(_targetRect);
            _forceClose = true;
            // 호스트(MainWindow)에 알려 _miniWindow 비워서 무효 창 참조 방지.
            Dismissed?.Invoke(this, EventArgs.Empty);
            Panel.ViewModel.Dispose();
            _slider.Dispose();
            Close();
        });
    }

    /// <summary>임계값 미달: 부드럽게 우하단 원위치로 복귀(닫지 않음).</summary>
    private void SnapBackFromDrag(Windows.Graphics.RectInt32 current)
    {
        _slider.Animate(current, _targetRect, 255, 255, 220, SlideEasing.EaseOut, null);
    }

    /// <summary>드래그 접기로 닫힐 때 발생, 호스트가 이에 맞춰 창 참조를 정리.</summary>
    public event EventHandler? Dismissed;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
