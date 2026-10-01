using Resenz.Core.Services;
using Resenz.App.ViewModels;

namespace Resenz.App.Services;

/// <summary>단축키 바인딩 시도의 결과 상태.</summary>
public enum HotkeyApplyStatus
{
    /// <summary>바인딩 성공.</summary>
    Ok,
    /// <summary>바인딩 해제됨.</summary>
    Cleared,
    /// <summary>형식 무효(수정키 없음/기본키 미인식).</summary>
    Invalid,
    /// <summary>앱 내 다른 동작과 충돌(Conflict는 충돌 동작).</summary>
    InternalConflict,
    /// <summary>다른 프로그램이 점유, 등록 실패.</summary>
    Occupied,
}

/// <summary>단축키 바인딩 시도 결과.</summary>
public sealed record HotkeyApplyResult(HotkeyApplyStatus Status, HotkeyAction? Conflict);

/// <summary>
/// 전역 단축키 관리: 각 동작을 메인 창 조작에 바인딩하고 설정을 설정 테이블에 영속화.
/// 저장 키: hotkey.<동작 이름>, 값은 "Ctrl+Alt+P" 형식. 빈 값은 바인딩 없음.
/// </summary>
public sealed partial class HotkeyManager : IDisposable
{
    private readonly HotkeyService _service = new();
    private readonly ResenzHost _host;
    private readonly MainWindow _window;

    public HotkeyManager(ResenzHost host, MainWindow window)
    {
        _host = host;
        _window = window;
    }

    public static readonly HotkeyAction[] AllActions =
    {
        HotkeyAction.ShowWindow,
        HotkeyAction.ToggleSystemProxy,
        HotkeyAction.ToggleTun,
        HotkeyAction.ToggleMiniPanel,
        HotkeyAction.ModeRule,
        HotkeyAction.ModeGlobal,
        HotkeyAction.ModeDirect,
    };

    public static string SettingKey(HotkeyAction action) => $"hotkey.{action}";

    /// <summary>동작의 한국어 표시 이름(충돌 안내용).</summary>
    public static string DisplayName(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowWindow => "메인 화면 표시",
        HotkeyAction.ToggleSystemProxy => "시스템 프록시 전환",
        HotkeyAction.ToggleTun => "TUN 모드 전환",
        HotkeyAction.ToggleMiniPanel => "미니 패널 전환",
        HotkeyAction.ModeRule => "규칙 모드",
        HotkeyAction.ModeGlobal => "전역 모드",
        HotkeyAction.ModeDirect => "직접 모드",
        _ => action.ToString(),
    };

    /// <summary>설정에서 전체 단축키를 불러와 등록.</summary>
    public async Task LoadAsync()
    {
        foreach (var action in AllActions)
        {
            var text = await _host.Settings.GetAsync(SettingKey(action)).ConfigureAwait(false);
            ApplyBinding(action, text);
        }
    }

    /// <summary>어떤 동작의 현재 바인딩 표시 텍스트 판단(바인딩 없으면 빈 값).</summary>
    public async Task<string> GetBindingAsync(HotkeyAction action)
        => await _host.Settings.GetAsync(SettingKey(action)).ConfigureAwait(false) ?? "";

    /// <summary>같은 조합키를 점유한 다른 동작을 찾음. 충돌 없으면 null 반환. normalized는 포맷된 바인딩 텍스트.</summary>
    public async Task<HotkeyAction?> FindConflictActionAsync(HotkeyAction action, string normalized)
    {
        foreach (var other in AllActions.Where(a => a != action))
        {
            var text = await GetBindingAsync(other).ConfigureAwait(false);
            if (string.Equals(text, normalized, StringComparison.OrdinalIgnoreCase))
                return other;
        }
        return null;
    }

    /// <summary>어떤 동작의 바인딩을 갱신하고 영속화. text가 비면 바인딩 해제. 성공 여부 반환.</summary>
    public async Task<bool> SetAsync(HotkeyAction action, string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            _service.Unregister(action);
            await _host.Settings.SetAsync(SettingKey(action), "").ConfigureAwait(false);
            return true;
        }

        if (!HotkeyParser.TryParse(text, out var mods, out var vk)) return false;

        var normalized = HotkeyParser.Format(mods, vk);
        var ok = _service.Register(action, mods, vk, CreateCallback(action));
        if (!ok) return false;

        await _host.Settings.SetAsync(SettingKey(action), normalized).ConfigureAwait(false);
        return true;
    }

    /// <summary>어떤 동작의 바인딩 해제 후 영속화.</summary>
    public async Task UnbindAsync(HotkeyAction action)
    {
        _service.Unregister(action);
        await _host.Settings.SetAsync(SettingKey(action), "").ConfigureAwait(false);
    }

    private void ApplyBinding(HotkeyAction action, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!HotkeyParser.TryParse(text, out var mods, out var vk)) return;
        _service.Register(action, mods, vk, CreateCallback(action));
    }

    private Action CreateCallback(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowWindow => () => _window.DispatcherQueue.TryEnqueue(_window.ShowAndActivate),
        HotkeyAction.ToggleSystemProxy => () => _window.DispatcherQueue.TryEnqueue(_window.HotkeyToggleSystemProxy),
        HotkeyAction.ToggleTun => () => _window.DispatcherQueue.TryEnqueue(_window.HotkeyToggleTun),
        HotkeyAction.ToggleMiniPanel => () => _window.DispatcherQueue.TryEnqueue(_window.ToggleMiniWindow),
        HotkeyAction.ModeRule => () => _window.DispatcherQueue.TryEnqueue(() => _ = _window.HotkeySelectModeAsync(0)),
        HotkeyAction.ModeGlobal => () => _window.DispatcherQueue.TryEnqueue(() => _ = _window.HotkeySelectModeAsync(1)),
        HotkeyAction.ModeDirect => () => _window.DispatcherQueue.TryEnqueue(() => _ = _window.HotkeySelectModeAsync(2)),
        _ => () => { },
    };

    public void Dispose() => _service.Dispose();
}
