using System.Net.Http.Headers;
using System.Text;
using Resenz.Data;

namespace Resenz.Core.Services;

public sealed record ProfileItem(
    long Id,
    string Name,
    string Kind,
    string? Source,
    string FilePath,
    string? SubscriptionUserInfo,
    bool IsActive,
    bool IsFree,
    int SortOrder,
    string UpdatedAt)
{
    public string KindText => Kind switch
    {
        "url" => "구독 링크",
        "file" => "로컬 파일",
        "manual" => "수동 설정",
        _ => Kind,
    };

    public string UpdatedText
    {
        get
        {
            if (DateTimeOffset.TryParse(UpdatedAt, out var ts))
                return ts.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            return UpdatedAt;
        }
    }
}

public interface IProfileService
{
    string ProfilesDirectory { get; }
    Task<IReadOnlyList<ProfileItem>> ListAsync(CancellationToken ct = default);
    Task<ProfileItem?> GetActiveAsync(CancellationToken ct = default);
    Task<ProfileItem> ImportFromUrlAsync(string url, string? name = null, CancellationToken ct = default);
    Task<ProfileItem> ImportFromFileAsync(string sourcePath, string? name = null, CancellationToken ct = default);
    Task<ProfileItem> CreateManualAsync(string name, string content, CancellationToken ct = default);
    Task<bool> RefreshAsync(long id, CancellationToken ct = default);
    Task<bool> SetActiveAsync(long id, CancellationToken ct = default);
    Task RenameAsync(long id, string name, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<string?> ReadContentAsync(long id, CancellationToken ct = default);
    Task<bool> SaveContentAsync(long id, string content, CancellationToken ct = default);
    Task<string> GenerateRuntimeConfigAsync(long id, RuntimeYamlOptions options, CancellationToken ct = default);
}

public sealed class ProfileService : IProfileService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly ProfileRepository _repo;
    private readonly ISettingsService _settings;

    public string ProfilesDirectory { get; }

    public ProfileService(ResenzDatabase database, ISettingsService settings, string profilesDirectory)
    {
        _repo = new ProfileRepository(database);
        _settings = settings;
        ProfilesDirectory = profilesDirectory;
        Directory.CreateDirectory(profilesDirectory);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            // 코어 실행 중에는 로컬 혼합 포트로 구독 다운로드(자체 프록시 활용).
            Proxy = DownloadProxy.Create(),
            UseProxy = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private const string DefaultUserAgent = "clash-verge/v1.0.0 Resenz/0.1";
    private const int DefaultTimeoutSeconds = 60;

    /// <summary>구독 요청의 User-Agent와 시간 초과를 읽음. 둘 다 설정에서 재정의 가능.</summary>
    private async Task<(string UserAgent, TimeSpan Timeout)> ReadRequestOptionsAsync(CancellationToken ct)
    {
        var ua = await _settings.GetAsync("core.subscriptionUserAgent").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ua)) ua = DefaultUserAgent;

        var seconds = await _settings.GetIntAsync("core.subscriptionTimeout", DefaultTimeoutSeconds).ConfigureAwait(false);
        if (seconds <= 0) seconds = DefaultTimeoutSeconds;

        return (ua!, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>User-Agent와 시간 초과를 붙여 구독 내용을 다운로드.</summary>
    private async Task<string> FetchContentAsync(string url, CancellationToken ct)
    {
        var (ua, timeout) = await ReadRequestOptionsAsync(ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(ua);
        using var response = await Http.SendAsync(request, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return result.Length == 0 ? "profile" : result;
    }

    public async Task<IReadOnlyList<ProfileItem>> ListAsync(CancellationToken ct = default)
    {
        var records = await _repo.ListAsync().ConfigureAwait(false);
        return records.Select(ToItem).ToList();
    }

    public async Task<ProfileItem?> GetActiveAsync(CancellationToken ct = default)
    {
        var record = await _repo.GetActiveAsync().ConfigureAwait(false);
        return record is null ? null : ToItem(record);
    }

    private static ProfileItem ToItem(ProfileRecord r)
        => new(r.Id, r.Name, r.Kind, r.Source, r.FilePath, r.SubscriptionUserInfo, r.IsActive, r.IsFree, r.SortOrder, r.UpdatedAt);

    public async Task<ProfileItem> ImportFromUrlAsync(string url, string? name = null, CancellationToken ct = default)
        => await ImportFromUrlCoreAsync(url, name, isFree: false, ct).ConfigureAwait(false);

    /// <summary>
    /// 첫 실행 시 무료 공개 구독 3개를 미리 등록. 기존 프로필이 하나라도 있으면 건너뛰고,
    /// 각 구독 내용을 즉시 내려받은 뒤 성공한 첫 구독을 활성화. 일부가 실패해도 나머지는 계속 등록.
    /// </summary>
    public async Task SeedFreeSubscriptionsAsync(CancellationToken ct = default)
    {
        var existing = await ListAsync(ct).ConfigureAwait(false);
        if (existing.Count > 0) return;

        (string Name, string Url)[] seeds =
        [
            ("무료 노드 · anaer", "https://raw.githubusercontent.com/anaer/Sub/main/clash.yaml"),
            ("무료 노드 · ermaozi", "https://raw.githubusercontent.com/ermaozi/get_subscribe/main/subscribe/clash.yml"),
            ("무료 노드 · free18", "https://raw.githubusercontent.com/free18/v2ray/main/c.yaml"),
        ];

        long? firstOk = null;
        foreach (var (name, url) in seeds)
        {
            try
            {
                var item = await ImportFromUrlCoreAsync(url, name, isFree: true, ct).ConfigureAwait(false);
                firstOk ??= item.Id;
            }
            catch
            {
                // 네트워크 실패 등으로 하나가 안 되어도 나머지는 등록. 파일이 없는 구독은 새로고침으로 복구.
            }
        }

        if (firstOk is not null)
            await SetActiveAsync(firstOk.Value, ct).ConfigureAwait(false);
    }

    private async Task<ProfileItem> ImportFromUrlCoreAsync(string url, string? name, bool isFree, CancellationToken ct)
    {
        var meta = await FetchSubscriptionMetaAsync(url, ct).ConfigureAwait(false);
        var displayName = string.IsNullOrWhiteSpace(name)
            ? meta.Name ?? $"구독 {DateTime.Now:MM-dd HH:mm}"
            : name!.Trim();
        var content = await FetchContentAsync(url, ct).ConfigureAwait(false);
        if (!LooksLikeClashConfig(content))
            throw new InvalidOperationException("다운로드한 내용이 유효한 Clash 설정이 아님(오류 페이지이거나 프록시 접근 필요할 수 있음).");

        var fileName = $"{SafeFileName(displayName)}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.yaml";
        var filePath = Path.Combine(ProfilesDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);

        var id = await _repo.InsertAsync(displayName, "url", url, filePath, isFree).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(meta.UserInfo))
            await _repo.UpdateContentAsync(id, meta.UserInfo).ConfigureAwait(false);

        var list = await ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 1 && !isFree)
            await _repo.SetActiveAsync(id).ConfigureAwait(false);

        return (await ListAsync(ct).ConfigureAwait(false)).First(p => p.Id == id);
    }

    private sealed record SubscriptionMeta(string? Name, string? UserInfo);

    /// <summary>
    /// 구독 응답 헤더의 메타정보 읽기: Profile-Title(base64, 최우선),
    /// Content-Disposition 의 파일 이름, 그리고 Subscription-Userinfo 사용량.
    /// </summary>
    private async Task<SubscriptionMeta> FetchSubscriptionMetaAsync(string url, CancellationToken ct)
    {
        try
        {
            var (ua, timeout) = await ReadRequestOptionsAsync(ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(ua);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            string? userInfo = null;
            if (response.Headers.TryGetValues("subscription-userinfo", out var values))
                userInfo = string.Join("; ", values);

            string? name = null;
            if (response.Headers.TryGetValues("profile-title", out var titleValues))
            {
                var raw = string.Join("", titleValues).Trim();
                name = DecodeProfileTitle(raw);
            }

            if (string.IsNullOrWhiteSpace(name)
                && response.Content.Headers.TryGetValues("Content-Disposition", out var cdValues))
            {
                name = ParseFileName(string.Join(";", cdValues));
            }

            return new SubscriptionMeta(name, userInfo);
        }
        catch
        {
            return new SubscriptionMeta(null, null);
        }
    }

    private static string? DecodeProfileTitle(string raw)
    {
        if (raw.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(raw["base64:".Length..].Trim());
                return System.Text.Encoding.UTF8.GetString(bytes).Trim();
            }
            catch
            {
                return null;
            }
        }
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static string? ParseFileName(string contentDisposition)
    {
        foreach (var part in contentDisposition.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["filename*=".Length..].Trim().Trim('"');
                var idx = value.IndexOf("''", StringComparison.Ordinal);
                if (idx >= 0) value = Uri.UnescapeDataString(value[(idx + 2)..]);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            if (trimmed.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["filename=".Length..].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    public async Task<ProfileItem> ImportFromFileAsync(string sourcePath, string? name = null, CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : name!.Trim();
        var content = await File.ReadAllTextAsync(sourcePath, ct).ConfigureAwait(false);

        var fileName = $"{SafeFileName(displayName)}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.yaml";
        var filePath = Path.Combine(ProfilesDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);

        var id = await _repo.InsertAsync(displayName, "file", sourcePath, filePath).ConfigureAwait(false);
        var list = await ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 1)
            await _repo.SetActiveAsync(id).ConfigureAwait(false);

        return (await ListAsync(ct).ConfigureAwait(false)).First(p => p.Id == id);
    }

    public async Task<ProfileItem> CreateManualAsync(string name, string content, CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "수동 설정" : name.Trim();
        var fileName = $"{SafeFileName(displayName)}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.yaml";
        var filePath = Path.Combine(ProfilesDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);

        var id = await _repo.InsertAsync(displayName, "manual", null, filePath).ConfigureAwait(false);
        var list = await ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 1)
            await _repo.SetActiveAsync(id).ConfigureAwait(false);

        return (await ListAsync(ct).ConfigureAwait(false)).First(p => p.Id == id);
    }

    public async Task<bool> RefreshAsync(long id, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null || item.Kind != "url" || string.IsNullOrEmpty(item.Source)) return false;

        try
        {
            var meta = await FetchSubscriptionMetaAsync(item.Source, ct).ConfigureAwait(false);
            var content = await FetchContentAsync(item.Source, ct).ConfigureAwait(false);
            // 내용이 실제 구독 설정인지 검증: 그러지 않으면 오류 페이지/HTML이 사용 가능한 설정을 덮어씀.
            if (!LooksLikeClashConfig(content)) return false;
            await File.WriteAllTextAsync(item.FilePath, content, ct).ConfigureAwait(false);
            await _repo.UpdateContentAsync(id, meta.UserInfo).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 내용이 Clash/mihomo 설정인지 대략 판별: proxies / proxy-providers / proxy-groups 중 하나는 반드시 포함해야 함
    /// 중 하나이고 HTML이 아님. 오류 페이지가 구독 파일에 쓰이는 것을 막는 데 사용.
    /// </summary>
    private static bool LooksLikeClashConfig(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('<')) return false;
        return content.Contains("proxies:", StringComparison.Ordinal)
            || content.Contains("proxy-providers:", StringComparison.Ordinal)
            || content.Contains("proxy-groups:", StringComparison.Ordinal);
    }

    public async Task<bool> SetActiveAsync(long id, CancellationToken ct = default)
    {
        await _repo.SetActiveAsync(id).ConfigureAwait(false);
        return true;
    }

    public Task RenameAsync(long id, string name, CancellationToken ct = default)
        => _repo.RenameAsync(id, name);

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null) return;

        await _repo.DeleteAsync(id).ConfigureAwait(false);
        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
        }
        catch
        {
        }

        var remaining = await ListAsync(ct).ConfigureAwait(false);
        if (remaining.Count > 0 && !remaining.Any(p => p.IsActive))
            await _repo.SetActiveAsync(remaining[0].Id).ConfigureAwait(false);
    }

    public async Task<string?> ReadContentAsync(long id, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null || !File.Exists(item.FilePath)) return null;
        return await File.ReadAllTextAsync(item.FilePath, ct).ConfigureAwait(false);
    }

    public async Task<bool> SaveContentAsync(long id, string content, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null) return false;

        try
        {
            await File.WriteAllTextAsync(item.FilePath, content, ct).ConfigureAwait(false);
            await _repo.UpdateContentAsync(id).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GenerateRuntimeConfigAsync(long id, RuntimeYamlOptions options, CancellationToken ct = default)
    {
        var content = await ReadContentAsync(id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            return MihomoConfigBuilder.BuildFallbackYaml(options);

        return MihomoConfigBuilder.BuildRuntimeYaml(content, options);
    }
}
