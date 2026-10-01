using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Resenz.Core.Models;

namespace Resenz.App.Mini;

public sealed partial class MiniProxyNodeViewModel : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    public partial int? Delay { get; set; }

    public string DelayText => Delay switch
    {
        null or < 0 => "",
        0 => "시간 초과",
        _ => $"{Delay} ms",
    };

    public string DelayKey => Delay switch
    {
        null or < 0 => "none",
        0 => "timeout",
        <= 150 => "good",
        <= 350 => "medium",
        _ => "bad",
    };

    public MiniProxyNodeViewModel(string name) => Name = name;

    partial void OnDelayChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayKey));
    }

    public override string ToString() => Name;
}

public sealed partial class MiniProxyGroupViewModel : ObservableObject
{
    public string Name { get; }

    public ObservableCollection<MiniProxyNodeViewModel> Nodes { get; } = new();

    [ObservableProperty]
    public partial MiniProxyNodeViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public MiniProxyGroupViewModel(string name) => Name = name;

    public void Fill(IReadOnlyList<string> members, IReadOnlyDictionary<string, ProxyItem> all, string? now)
    {
        var names = new List<string>();
        foreach (var member in members)
        {
            if (all.ContainsKey(member)) names.Add(member);
        }

        var unchanged = names.Count == Nodes.Count;
        if (unchanged)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (!string.Equals(Nodes[i].Name, names[i], StringComparison.Ordinal))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (!unchanged)
        {
            Nodes.Clear();
            foreach (var name in names) Nodes.Add(new MiniProxyNodeViewModel(name));
        }

        foreach (var node in Nodes)
        {
            if (all.TryGetValue(node.Name, out var item)) node.Delay = item.Delay;
        }

        // API의 현재 선택 항목을 기준으로 선택 동기화(패널 간 노드 전환 때 적용),
        // now 무효할 때는 기존 선택을 유지해 재배열/새로고침이 선택을 지우지 않게 함. 측정은 ApplyDelays로 처리하며 여기를 거치지 않음.
        var target = now is null ? null : Nodes.FirstOrDefault(n => n.Name == now);
        if (target is not null)
            Selected = target;
        else if (Selected is null || !Nodes.Contains(Selected))
            Selected = Nodes.FirstOrDefault();

        SortByDelay();
    }

    /// <summary>지연 정렬: 유효 지연이 앞에 오며 오름차순, 시간 초과/미측정은 뒤로.</summary>
    public void SortByDelay()
    {
        var sorted = Nodes
            .OrderBy(n => n.Delay is > 0 ? 0 : 1)
            .ThenBy(n => n.Delay is > 0 ? n.Delay!.Value : int.MaxValue)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inOrder = sorted.Count == Nodes.Count;
        if (inOrder)
        {
            for (var i = 0; i < sorted.Count; i++)
            {
                if (!ReferenceEquals(Nodes[i], sorted[i]))
                {
                    inOrder = false;
                    break;
                }
            }
        }
        if (inOrder) return;

        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Nodes.IndexOf(sorted[i]);
            if (current != i) Nodes.Move(current, i);
        }

        // Move ComboBox가 SelectedItem을 잃게 되므로 같은 참조를 강제로 다시 밀어 넣어 선택을 복원.
        OnPropertyChanged(nameof(Selected));
    }

    public void ApplyDelays(IReadOnlyDictionary<string, int> delays)
    {
        foreach (var node in Nodes)
            node.Delay = delays.TryGetValue(node.Name, out var d) && d > 0 ? d : 0;

        SortByDelay();
    }
}
