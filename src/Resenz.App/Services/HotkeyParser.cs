using System.Text;

namespace Resenz.App.Services;

/// <summary>바인딩 가능한 전역 단축키 동작.</summary>
public enum HotkeyAction
{
    ShowWindow,
    ToggleSystemProxy,
    ToggleTun,
    ToggleMiniPanel,
    ModeRule,
    ModeGlobal,
    ModeDirect,
}

/// <summary>
/// 단축키 해석과 포맷. 내부 표현은 다음으로 통일:
/// "Ctrl+Alt+Shift+Win+Key" 형식 문자열(수정키는 고정 순서).
/// </summary>
public static class HotkeyParser
{
    // Win32 RegisterHotKey와 일치하는 수정키 비트.
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>해석 "Ctrl+Alt+P" 형식. 실패 시 false 반환.</summary>
    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var key = "";
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win":
                case "super": modifiers |= MOD_WIN; break;
                default:
                    if (key.Length > 0) return false; // 기본키 여러 개
                    key = p;
                    break;
            }
        }

        if (key.Length == 0) return false;
        vk = KeyToVk(key);
        // 기본키는 인식 가능한 문자/숫자/기능키여야 하고 최소 한 개의 수정키 필요(벗겨진 키의 선점 방지).
        if (vk == 0 || modifiers == 0) return false;
        return true;
    }

    private static uint KeyToVk(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var fn)
            && fn is >= 1 and <= 24)
        {
            return (uint)(0x70 + fn - 1); // VK_F1 = 0x70
        }

        // 키패드 숫자 numpad0-9
        if (key.Length == 7 && key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[6..], out var num) && num is >= 0 and <= 9)
        {
            return (uint)(0x60 + num); // VK_NUMPAD0 = 0x60
        }

        return key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "numlock" => 0x90,
            "`" or "backquote" => 0xC0,
            "-" or "minus" => 0xBD,
            "=" or "equal" => 0xBB,
            "[" or "bracketleft" => 0xDB,
            "]" or "bracketright" => 0xDD,
            "\\" or "backslash" => 0xDC,
            ";" or "semicolon" => 0xBA,
            "'" or "quote" => 0xDE,
            "," or "comma" => 0xBC,
            "." or "period" => 0xBE,
            "/" or "slash" => 0xBF,
            _ => 0,
        };
    }

    /// <summary>고정 순서의 표시 텍스트로 포맷. 수정키 없으면 빈 값 반환.</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var sb = new StringBuilder();
        if ((modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & MOD_ALT) != 0) sb.Append("Alt+");
        if ((modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((modifiers & MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(VkToKey(vk));
        return sb.ToString();
    }

    private static string VkToKey(uint vk)
    {
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => vk.ToString(),
        };
    }
}
