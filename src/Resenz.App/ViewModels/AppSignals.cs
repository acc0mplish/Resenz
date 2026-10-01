namespace Resenz.App.ViewModels;

/// <summary>
/// 앱 내 뷰 간·창 간 전역 상태 브로드캐스트. 메인 패널과 미니 패널은 각자 독립된 ViewModel을 가지며
/// 어느 한쪽의 변경(모드, 시스템 프록시, TUN, 프록시 그룹/노드)도 여기서 브로드캐스트되며 다른 쪽이 구독 후 새로고침.
/// </summary>
public static class AppSignals
{
    /// <summary>프록시 그룹/노드 데이터를 다시 가져와야 함(구독 새로고침, 설정 리로드, 노드 전환 등).</summary>
    public static event EventHandler? ProxiesChanged;

    /// <summary>모드, 시스템 프록시, TUN 등 실행 토글이 변경됨.</summary>
    public static event EventHandler? SwitchesChanged;

    /// <summary>메인 창 표시/숨김(트레이로 숨기거나 미니 패널만 사용할 때 false). 페이지는 이에 따라 백그라운드 폴링을 일시중지.</summary>
    public static event EventHandler<bool>? MainWindowVisibilityChanged;

    public static void RaiseProxiesChanged() => ProxiesChanged?.Invoke(null, EventArgs.Empty);

    public static void RaiseSwitchesChanged() => SwitchesChanged?.Invoke(null, EventArgs.Empty);

    public static void RaiseMainWindowVisibility(bool visible) => MainWindowVisibilityChanged?.Invoke(null, visible);
}
