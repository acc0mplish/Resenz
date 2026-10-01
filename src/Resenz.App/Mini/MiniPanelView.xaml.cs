using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Resenz.App.ViewModels;

namespace Resenz.App.Mini;

public sealed partial class MiniPanelView : UserControl
{
    public MiniPanelViewModel ViewModel { get; }

    public event EventHandler? OpenFullRequested;
    public event EventHandler? CollapseRequested;

    private bool _modeLoading;

    public MiniPanelView()
    {
        ViewModel = new MiniPanelViewModel(
            global::Resenz.App.AppHost.Host,
            DispatcherQueue);
        InitializeComponent();
        ViewModel.ModeSelector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModeSelectorViewModel.SelectedIndex)
                or nameof(ModeSelectorViewModel.SystemProxyOn)
                or nameof(ModeSelectorViewModel.TunOn))
            {
                SyncModeBar();
            }
        };

        // 드래그 접기 제스처: 패널 전체를 끌 수 있지만 대화형 컨트롤 위에선 컨트롤이 클릭을 우선 처리.
        RootGrid.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(OnRootPointerPressed),
            handledEventsToo: true);
        RootGrid.AddHandler(
            PointerMovedEvent,
            new PointerEventHandler(OnRootPointerMoved),
            handledEventsToo: true);
        RootGrid.AddHandler(
            PointerReleasedEvent,
            new PointerEventHandler(OnRootPointerReleased),
            handledEventsToo: true);
        RootGrid.AddHandler(
            PointerCanceledEvent,
            new PointerEventHandler(OnRootPointerCanceled),
            handledEventsToo: true);
    }

    /// <summary>드래그 제스처 시작(이동량이 임계값 초과, 드래그 진입).</summary>
    public event EventHandler? DragGestureStarted;

    /// <summary>드래그 제스처 이동(마우스 위치 변화 시 발동).</summary>
    public event EventHandler? DragGestureMoved;

    /// <summary>드래그 제스처 종료(놓기/취소), 접을지 복귀할지는 호스트가 결정.</summary>
    public event EventHandler? DragGestureEnded;

    /// <summary>누른 뒤 이동량이 임계값을 넘어야 드래그로 봄. 임계값 이내 가벼운 클릭은 컨트롤이 정상 처리(클릭).</summary>
    private const double DragThresholdDip = 8;

    private bool _dragArmed;
    private bool _dragActive;
    private uint? _dragPointerId;
    private Windows.Foundation.Point _pressPoint;

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragArmed || _dragActive) return;
        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
        _dragArmed = true;
        _dragPointerId = e.Pointer.PointerId;
        _pressPoint = e.GetCurrentPoint(RootGrid).Position;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;
        if (_dragActive)
        {
            DragGestureMoved?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!_dragArmed) return;

        var pos = e.GetCurrentPoint(RootGrid).Position;
        double dx = pos.X - _pressPoint.X;
        double dy = pos.Y - _pressPoint.Y;
        if (Math.Abs(dx) <= DragThresholdDip && Math.Abs(dy) <= DragThresholdDip) return;

        // 누른 뒤 이동량이 임계값을 넘어야 드래그로 봄. 임계값 안의 짧은 클릭은 컨트롤이 정상적으로 처리하도록
        // 포인터를 인계해 이후 이벤트는 이 컨트롤로만 전달.
        _dragArmed = false;
        _dragActive = true;
        RootGrid.CapturePointer(e.Pointer);
        DragGestureStarted?.Invoke(this, EventArgs.Empty);
        DragGestureMoved?.Invoke(this, EventArgs.Empty);
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;
        if (_dragActive)
        {
            _dragActive = false;
            _dragArmed = false;
            _dragPointerId = null;
            RootGrid.ReleasePointerCapture(e.Pointer);
            DragGestureEnded?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // 드래그에 들어가지 않으면 가벼운 클릭으로 봄: 인계하지 않고 컨트롤(버튼 등)이 Click을 정상 수신.
            _dragArmed = false;
            _dragPointerId = null;
        }
    }

    private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;
        bool wasActive = _dragActive;
        _dragActive = false;
        _dragArmed = false;
        _dragPointerId = null;
        if (wasActive) DragGestureEnded?.Invoke(this, EventArgs.Empty);
    }

    public void SetSolidBackground(bool solid)
    {
        SolidBackdrop.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task InitializeAsync()
    {
        await ViewModel.LoadModeAsync();
        SyncModeBar();
    }

    /// <summary>패널이 표시될 때마다 모드와 토글 상태를 다시 가져와 메인 창과의 일치를 보장.</summary>
    public async Task RefreshOnShowAsync()
    {
        await ViewModel.RefreshOnShowAsync();
        SyncModeBar();
    }

    private void SyncModeBar()
    {
        _modeLoading = true;
        try
        {
            var index = ViewModel.ModeSelector.SelectedIndex;
            ModeRuleButton.IsChecked = index == 0;
            ModeGlobalButton.IsChecked = index == 1;
            ModeDirectButton.IsChecked = index == 2;

            var selector = ViewModel.ModeSelector;
            if (SystemProxySwitch.IsOn != selector.SystemProxyOn)
                SystemProxySwitch.IsOn = selector.SystemProxyOn;
            if (TunSwitch.IsOn != selector.TunOn)
                TunSwitch.IsOn = selector.TunOn;
        }
        finally
        {
            _modeLoading = false;
        }
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modeLoading) return;
        if (sender is not ToggleButton button) return;
        if (button.Tag is not string tag || !int.TryParse(tag, out var index)) return;

        if (ViewModel.ModeSelector.SelectedIndex == index)
        {
            SyncModeBar();
            return;
        }

        await ViewModel.SelectModeAsync(index);
        SyncModeBar();
    }

    private void SystemProxy_Toggled(object sender, RoutedEventArgs e)
    {
        if (_modeLoading) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ViewModel.ModeSelector.SystemProxyOn) return;
        _ = ViewModel.SetSystemProxyAsync(toggle.IsOn);
    }

    private void Tun_Toggled(object sender, RoutedEventArgs e)
    {
        if (_modeLoading) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ViewModel.ModeSelector.TunOn) return;
        _ = ViewModel.SetTunAsync(toggle.IsOn);
    }

    private void Node_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Tag is not MiniProxyGroupViewModel group) return;
        if (combo.SelectedItem is not MiniProxyNodeViewModel node) return;
        // 컬렉션 재배열이나 프로그램적 Selected 동기화 시 ComboBox는 SelectionChanged를 발생시킴.
        // 사용자가 실제로 다시 선택한 경우(선택 참조가 실제로 변경됨)에만 처리. 그렇지 않으면 ProxiesChanged와 무한 리로드 루프가 생김.
        if (ReferenceEquals(group.Selected, node)) return;
        group.Selected = node;
        _ = ViewModel.SelectNodeAsync(group, node);
    }

    private void TestGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not MiniProxyGroupViewModel group) return;
        _ = ViewModel.TestGroupAsync(group);
    }

    private void OpenFull_Click(object sender, RoutedEventArgs e)
        => OpenFullRequested?.Invoke(this, EventArgs.Empty);

    private void Collapse_Click(object sender, RoutedEventArgs e)
        => CollapseRequested?.Invoke(this, EventArgs.Empty);
}
