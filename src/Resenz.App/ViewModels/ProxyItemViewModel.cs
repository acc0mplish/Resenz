using CommunityToolkit.Mvvm.ComponentModel;
using Resenz.Core.Models;

namespace Resenz.App.ViewModels;

public sealed partial class ProxyItemViewModel : ObservableObject
{
    public string Name { get; }
    public string Type { get; }
    public IReadOnlyList<string> Members { get; }
    public string GroupName { get; }

    public Action<ProxyItemViewModel>? SelectRequested { get; set; }
    public Action<ProxyItemViewModel>? TestRequested { get; set; }

    [ObservableProperty]
    public partial bool IsAlive { get; set; }

    [ObservableProperty]
    public partial int? Delay { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public bool IsGroup { get; }

    public ProxyItemViewModel(string name, string type, bool alive, int? delay, bool selected,
        IReadOnlyList<string>? members = null, bool isGroup = false, string groupName = "")
    {
        Name = name;
        Type = type;
        IsAlive = alive;
        Delay = delay;
        IsSelected = selected;
        Members = members ?? Array.Empty<string>();
        IsGroup = isGroup;
        GroupName = groupName;
    }
    public void SetSelected(bool value) => IsSelected = value;

    /// <summary>이름 앞의 깃발/아이콘(지역 표시 문자 쌍 또는 선행 emoji)은 알아보기 쉽게 따로 확대 렌더링.</summary>
    public string IconText => SplitIcon(Name).Icon;

    /// <summary>앞 아이콘 제거 후 이름.</summary>
    public string DisplayName => SplitIcon(Name).Text;

    private static (string Icon, string Text) SplitIcon(string name)
    {
        if (string.IsNullOrEmpty(name)) return ("", name ?? "");

        var i = 0;
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(name);
        if (elements.Length == 0) return ("", name);

        // 첫 자소 클러스터를 취함. 지역 표시 문자 쌍(깃발)이면 바로 뒤따르는 변형 선택자까지 합침.
        var firstEnd = elements.Length > 1 ? elements[1] : name.Length;
        var first = name[..firstEnd];

        // 지역 표시 문자 U+1F1E6..U+1F1FF: 둘씩 짝을 이뤄 국기를 이룸.
        var isFlag = first.Length >= 2
            && char.IsSurrogatePair(first, 0)
            && char.ConvertToUtf32(first, 0) is >= 0x1F1E6 and <= 0x1F1FF;

        i = firstEnd;
        if (isFlag && elements.Length > 2)
        {
            // 두 번째 지역 표시 문자 병합(같은 자소 클러스터에 보통 포함되지만 일부 입력은 분리되어 있음).
            var secondEnd = elements.Length > 2 ? elements[3] : name.Length;
            var second = name[firstEnd..secondEnd];
            if (second.Length >= 2 && char.IsSurrogatePair(second, 0)
                && char.ConvertToUtf32(second, 0) is >= 0x1F1E6 and <= 0x1F1FF)
            {
                i = secondEnd;
            }
        }

        var icon = name[..i].Trim();
        var text = name[i..].Trim();
        return (icon, text.Length == 0 ? name : text);
    }

    public string DelayText => IsTesting ? "" : Format.Delay(Delay);
    public string DelayKey
    {
        get
        {
            if (IsTesting) return "testing";
            if (Delay is null or < 0) return "none";
            if (Delay == 0) return "timeout";
            if (Delay <= 150) return "good";
            if (Delay <= 350) return "medium";
            return "bad";
        }
    }

    public string TypeText => string.IsNullOrEmpty(Type) ? "노드" : Type.ToLowerInvariant() switch
    {
        "selector" => "선택자",
        "urltest" => "자동 측정",
        "fallback" => "장애 조치",
        "loadbalance" => "로드 밸런싱",
        "relay" => "체인 프록시",
        "direct" => "직접 연결",
        "reject" => "거부",
        "dns" => "DNS",
        var t => t,
    };

    public void Update(ProxyItem item, bool selected)
    {
        IsAlive = item.Alive;
        Delay = item.Delay;
        SetSelected(selected);
    }

    partial void OnDelayChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayKey));
    }

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayKey));
    }
}
