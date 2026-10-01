using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Resenz.App.Controls;

/// <summary>
/// 항상 활성인 아크릴 배경: IsInputActive를 true로 고정,
/// 창이 포커스를 잃거나 다른 창에 가려져도 아래 창 내용을 계속 샘플링·흐리게 함,
/// 기본처럼 배경화면/단색으로 강등되는 대신.
/// </summary>
public sealed class AlwaysActiveAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, Target> _targets = new();
    private bool _disposed;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        var config = BuildConfig(xamlRoot);
        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        controller.SetSystemBackdropConfiguration(config);
        controller.AddSystemBackdropTarget(connectedTarget);

        _targets[connectedTarget] = new Target(controller, config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_targets.Remove(disconnectedTarget, out var target))
        {
            try
            {
                target.Controller.RemoveSystemBackdropTarget(disconnectedTarget);
            }
            catch
            {
            }
            target.Controller.Dispose();
        }
    }

    /// <summary>
    /// 기본 클래스가 테마/입력 상태 변화 때 콜백. 반드시 재정의: 기본 구현은 현재 대상으로 기본 설정을 다시 만들어
    /// 대상이 소재 교체 후 이미 무효화됐으면 ArgumentException(잘못된 매개변수 target) 발생.
    /// 여기서는 최신 테마로 기존 컨트롤러 설정을 갱신하는 방식으로 변경.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (_targets.TryGetValue(target, out var entry))
        {
            entry.Config.Theme = ResolveTheme(xamlRoot);
        }
    }

    /// <summary>아직 연결된 모든 컨트롤러를 해제. WinUI는 SystemBackdrop 교체 시 이전 대상을 자동으로 끊지 않으므로
    /// 명시적으로 호출해야 함. 그러지 않으면 배경 소재를 바꿀 때마다 DesktopAcrylicController가 하나씩 누수됨.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _targets)
        {
            try
            {
                kv.Value.Controller.RemoveSystemBackdropTarget(kv.Key);
            }
            catch
            {
            }
            kv.Value.Controller.Dispose();
        }
        _targets.Clear();
    }

    private static SystemBackdropConfiguration BuildConfig(XamlRoot xamlRoot) => new()
    {
        IsInputActive = true,
        Theme = ResolveTheme(xamlRoot),
    };

    private static SystemBackdropTheme ResolveTheme(XamlRoot xamlRoot) =>
        xamlRoot.Content is FrameworkElement fe
            ? fe.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default,
            }
            : SystemBackdropTheme.Default;

    private sealed class Target
    {
        public Target(DesktopAcrylicController controller, SystemBackdropConfiguration config)
        {
            Controller = controller;
            Config = config;
        }

        public DesktopAcrylicController Controller { get; }

        public SystemBackdropConfiguration Config { get; }
    }
}
