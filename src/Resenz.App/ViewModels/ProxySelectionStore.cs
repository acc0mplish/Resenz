using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

/// <summary>
/// 사용자가 프록시 그룹마다 수동으로 고른 노드를 기억했다가 코어 시작 후 자동 복원.
/// 설정 테이블에 저장(키 core.proxySelection), 형식은 줄마다 "그룹 이름\t노드 이름".
/// </summary>
public static class ProxySelectionStore
{
    private const string Key = "core.proxySelection";

    public static async Task<Dictionary<string, string>> LoadAsync(ISettingsService settings)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var raw = await settings.GetAsync(Key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return map;

            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.IndexOf('\t');
                if (idx <= 0 || idx >= line.Length - 1) continue;
                var group = line[..idx].Trim();
                var node = line[(idx + 1)..].Trim();
                if (group.Length > 0 && node.Length > 0) map[group] = node;
            }
        }
        catch
        {
        }
        return map;
    }

    public static async Task SaveAsync(ISettingsService settings, IReadOnlyDictionary<string, string> map)
    {
        try
        {
            var text = string.Join("\n", map.Select(kv => $"{kv.Key}\t{kv.Value}"));
            await settings.SetAsync(Key, text).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>한 그룹의 선택 기록(읽고 고쳐 씀).</summary>
    public static async Task RememberAsync(ISettingsService settings, string group, string node)
    {
        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(node)) return;
        var map = await LoadAsync(settings).ConfigureAwait(false);
        map[group] = node;
        await SaveAsync(settings, map).ConfigureAwait(false);
    }
}
