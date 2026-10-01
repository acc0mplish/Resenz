using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace Resenz.Core.Services;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record KernelRelease(
    string Tag,
    string Version,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record KernelUpdateInfo(
    bool Installed,
    string? CurrentVersion,
    string? LatestVersion,
    bool HasUpdate,
    string? Channel,
    string? Error);

public interface IKernelUpdateService
{
    string BinaryPath { get; }
    string WintunPath { get; }
    string? GetInstalledVersion();
    Task<KernelUpdateInfo> CheckAsync(string? channel = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default);
    Task<bool> DownloadAndInstallAsync(string tag, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<bool> EnsureWintunAsync(CancellationToken ct = default);
    Task<bool> EnsureGeodataAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>내장 리소스 디렉터리에서 코어/wintun/geodata를 해제(없을 때만 복사). 해제한 파일 수 반환.</summary>
    int ProvisionFromBundle(string bundleDirectory);
}

public sealed class KernelUpdateService : IKernelUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/MetaCubeX/mihomo/releases";
    private const string WintunUrl = "https://wintun.net/builds/wintun-0.14.1.zip";

    private readonly HttpClient _http;
    private readonly string _coreDirectory;
    private readonly ISettingsService? _settings;

    public string BinaryPath { get; }
    public string WintunPath { get; }

    public KernelUpdateService(string coreDirectory, ISettingsService? settings = null)
    {
        _coreDirectory = coreDirectory;
        _settings = settings;
        Directory.CreateDirectory(coreDirectory);
        BinaryPath = Path.Combine(coreDirectory, "mihomo.exe");
        WintunPath = Path.Combine(coreDirectory, "wintun.dll");

        _http = new HttpClient(new HttpClientHandler
        {
            // 코어 실행 중에는 로컬 혼합 포트로 다운로드(자체 프록시로 GitHub 접근).
            Proxy = DownloadProxy.Create(),
            UseProxy = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Resenz/0.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// 내장 다운로드 우회 경로. GitHub 직접 연결이 안정적인 환경이 기본이라
    /// 별도 미러를 두지 않으며, 설정에서 사용자 지정 프록시를 입력하면 그것을 우선 사용.
    /// </summary>
    private static readonly string[] BuiltinProxies = { };

    /// <summary>
    /// 후보 가속 접두사 목록 반환, 끝의 null은 직접 연결 폴백.
    /// auto/빈 값 = 내장 소스 전부 순서대로 시도 후 직접 연결. direct = 직접 연결만. 사용자 지정 = 소스 뒤 직접 연결.
    /// </summary>
    private async Task<IReadOnlyList<string?>> GetProxyCandidatesAsync(CancellationToken ct)
    {
        if (_settings is null) return new string?[] { null };

        var value = (await _settings.GetAsync("core.githubProxy").ConfigureAwait(false))?.Trim();
        if (string.Equals(value, "direct", StringComparison.OrdinalIgnoreCase))
            return new string?[] { null };

        if (string.IsNullOrEmpty(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var list = BuiltinProxies.Select(p => (string?)p).ToList();
            list.Add(null);
            return list;
        }

        return new string?[] { value!.TrimEnd('/'), null };
    }

    /// <summary>설정에 따라 GitHub 도메인을 가속 프록시 접두사 형태로 변환. GitHub 외 주소는 그대로 반환.</summary>
    private static string ApplyProxy(string proxy, string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        var host = url["https://".Length..];
        var isGithub = host.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("api.github.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("codeload.github.com/", StringComparison.OrdinalIgnoreCase);
        return isGithub ? $"{proxy}/{url}" : url;
    }

    public async Task<KernelUpdateInfo> CheckAsync(string? channel = null, CancellationToken ct = default)
    {
        var current = GetInstalledVersion();
        try
        {
            var releases = await FetchReleasesAsync(ct).ConfigureAwait(false);
            var assetPattern = GetAssetPattern();

            var latestTag = releases
                .Select(r => r.Tag)
                .FirstOrDefault(t => IsStableTag(t));

            if (string.IsNullOrEmpty(latestTag))
                return new KernelUpdateInfo(current is not null, current, null, false, channel, "버전 목록 획득 실패");

            var hasUpdate = current is null || !string.Equals(Normalize(current), Normalize(latestTag), StringComparison.OrdinalIgnoreCase);
            return new KernelUpdateInfo(
                Installed: current is not null,
                CurrentVersion: current,
                LatestVersion: latestTag,
                HasUpdate: hasUpdate,
                Channel: channel,
                Error: null);
        }
        catch (Exception ex)
        {
            return new KernelUpdateInfo(current is not null, current, null, false, channel, ex.Message);
        }
    }

    private static string Normalize(string version) => version.TrimStart('v', 'V').Split(' ')[0];

    /// <summary>선택 가능 버전 나열(안정 tag 내림차순).</summary>
    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default)
    {
        try
        {
            var releases = await FetchReleasesAsync(ct).ConfigureAwait(false);
            return releases.Select(r => r.Tag).Where(IsStableTag).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsStableTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (tag.Contains("prerelease", StringComparison.OrdinalIgnoreCase)) return false;
        if (tag.Contains("alpha", StringComparison.OrdinalIgnoreCase)) return false;
        if (tag.Contains("beta", StringComparison.OrdinalIgnoreCase)) return false;
        var normalized = tag.TrimStart('v', 'V');
        return Version.TryParse(normalized.Split('-')[0], out _);
    }

    public string? GetInstalledVersion()
    {
        var versionFile = Path.Combine(_coreDirectory, "version.txt");
        if (File.Exists(versionFile))
        {
            try
            {
                var content = File.ReadAllText(versionFile).Trim();
                if (content.Length > 0) return content;
            }
            catch
            {
            }
        }
        return File.Exists(BinaryPath) ? "unknown" : null;
    }

    private async Task<List<KernelRelease>> FetchReleasesAsync(CancellationToken ct)
    {
        var apiUrl = $"{ReleasesApi}?per_page=30";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proxy in await GetProxyCandidatesAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var url = proxy is null ? apiUrl : ApplyProxy(proxy, apiUrl);
            if (!seen.Add(url)) continue;
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var list = ParseReleases(doc);
                if (list.Count > 0) return list;
            }
            catch
            {
                // 이 소스는 실패했으므로 다음 소스로 재시도.
            }
        }
        return new List<KernelRelease>();
    }

    private static List<KernelRelease> ParseReleases(JsonDocument doc)
    {
        var list = new List<KernelRelease>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tag)) continue;

            var assets = new List<ReleaseAsset>();
            if (release.TryGetProperty("assets", out var assetArr) && assetArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetArr.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                    assets.Add(new ReleaseAsset(name, url, size));
                }
            }

            list.Add(new KernelRelease(tag, tag, assets));
        }
        return list;
    }

    private static string GetAssetPattern()
    {
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "windows-arm64",
            System.Runtime.InteropServices.Architecture.X86 => "windows-386",
            _ => "windows-amd64",
        };
    }

    public async Task<bool> DownloadAndInstallAsync(string tag, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var releases = await FetchReleasesAsync(ct).ConfigureAwait(false);
            var release = releases.FirstOrDefault(r => string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase))
                          ?? releases.FirstOrDefault(r => string.Equals(Normalize(r.Tag), Normalize(tag), StringComparison.OrdinalIgnoreCase));
            if (release is null) return false;

            var pattern = GetAssetPattern();
            var candidates = release.Assets
                .Where(a => a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                            && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var asset = candidates.FirstOrDefault(a => a.Name.Contains("compatible", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(a => !a.Name.Contains("-go", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault();

            if (asset is null) return false;

            var archivePath = Path.Combine(_coreDirectory, "mihomo.download.zip");
            await DownloadFileAsync(asset.DownloadUrl, archivePath, progress, ct).ConfigureAwait(false);

            var tempBinary = Path.Combine(_coreDirectory, "mihomo.new.exe");
            await ExtractKernelFromZipAsync(archivePath, tempBinary, ct).ConfigureAwait(false);
            try { File.Delete(archivePath); } catch { }

            var info = new FileInfo(tempBinary);
            if (!info.Exists || info.Length < 1024 * 100)
            {
                try { File.Delete(tempBinary); } catch { }
                return false;
            }

            try
            {
                if (File.Exists(BinaryPath)) File.Delete(BinaryPath);
            }
            catch
            {
            }
            File.Move(tempBinary, BinaryPath, overwrite: true);

            await File.WriteAllTextAsync(Path.Combine(_coreDirectory, "version.txt"), release.Tag, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        Exception? last = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proxy in await GetProxyCandidatesAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var target = proxy is null ? url : ApplyProxy(proxy, url);
            // GitHub 외 주소는 소스 변환 후에도 그대로이므로 중복 제거해 같은 URL 재시도를 피함.
            if (!seen.Add(target)) continue;
            try
            {
                using var response = await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(destination);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0) progress?.Report((double)received / total);
                }

                // 내용이 너무 작으면 실패로 간주(가속 소스가 오류 페이지를 자주 반환)하고 다음 소스로 교체.
                if (total > 0 && received < total)
                {
                    last = new IOException($"다운로드 불완전: {{received}/{total}");
                    continue;
                }
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            }
        }

        throw last ?? new IOException("모든 다운로드 소스 실패");
    }

    private static async Task ExtractKernelFromZipAsync(string zipPath, string destination, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidOperationException("압축판에서 mihomo.exe 미발견");

        await using var source = entry.Open();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 내장 리소스 디렉터리에서 코어/wintun/geodata를 데이터 디렉터리로 해제(대상이 이미 있으면 건너뜀).
    /// 용도:"내장 코어와 지리 데이터베이스"릴리스판을 포함해 첫 시작 때 네트워크 다운로드 회피.
    /// </summary>
    public int ProvisionFromBundle(string bundleDirectory)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory) || !Directory.Exists(bundleDirectory)) return 0;

        var copied = 0;
        void CopyIfMissing(string sourceName, string targetPath)
        {
            try
            {
                var src = Path.Combine(bundleDirectory, sourceName);
                if (!File.Exists(src)) return;
                if (File.Exists(targetPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(src, targetPath, overwrite: false);
                copied++;
            }
            catch
            {
            }
        }

        // 코어와 wintun
        CopyIfMissing("mihomo.exe", BinaryPath);
        CopyIfMissing("wintun.dll", WintunPath);

        // 코어 버전 표기(내장 패키지가 제공 시)
        var versionSrc = Path.Combine(bundleDirectory, "version.txt");
        var versionDst = Path.Combine(_coreDirectory, "version.txt");
        if (File.Exists(versionSrc) && !File.Exists(versionDst))
        {
            try { File.Copy(versionSrc, versionDst, overwrite: false); copied++; } catch { }
        }

        // 지리 데이터베이스
        foreach (var name in GeodataFileNames)
            CopyIfMissing(name, Path.Combine(_coreDirectory, name));

        return copied;
    }

    private static readonly string[] GeodataFileNames =
    {
        "geoip.metadb", "geosite.dat", "geoip.dat", "ASN.mmdb", "country.mmdb",
    };

    public async Task<bool> EnsureGeodataAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // 지리 데이터(GeoIP/GeoSite)는 용량이 커서 코어가 첫 실행 때 자동으로 내려받음.
        // 여기서 능동적으로 미리 받아 첫 시작이 다운로드 대기로 시간 초과되지 않게 함.
        var files = new (string Name, string Url)[]
        {
            ("geoip.metadb", "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.metadb"),
            ("geosite.dat", "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geosite.dat"),
            ("geoip.dat", "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.dat"),
            ("ASN.mmdb", "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/GeoLite2-ASN.mmdb"),
            ("country.mmdb", "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/country.mmdb"),
        };

        var missing = files.Where(f => !File.Exists(Path.Combine(_coreDirectory, f.Name))).ToList();
        if (missing.Count == 0) return true;

        var done = 0;
        var ok = true;
        foreach (var file in missing)
        {
            var target = Path.Combine(_coreDirectory, file.Name);
            try
            {
                var sub = progress is null
                    ? null
                    : new Progress<double>(p => progress.Report((done + p) / missing.Count));
                await DownloadFileAsync(file.Url, target, sub, ct).ConfigureAwait(false);
                done++;
            }
            catch
            {
                ok = false;
            }
        }
        progress?.Report(1);
        return ok;
    }

    public async Task<bool> EnsureWintunAsync(CancellationToken ct = default)
    {
        if (File.Exists(WintunPath)) return true;
        try
        {
            var zipPath = Path.Combine(_coreDirectory, "wintun.zip");
            await DownloadFileAsync(WintunUrl, zipPath, null, ct).ConfigureAwait(false);

            using var archive = ZipFile.OpenRead(zipPath);
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X86 => "x86",
                _ => "amd64",
            };

            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Contains($"/{arch}/", StringComparison.OrdinalIgnoreCase) &&
                e.Name.Equals("wintun.dll", StringComparison.OrdinalIgnoreCase));

            entry ??= archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("wintun.dll", StringComparison.OrdinalIgnoreCase));

            if (entry is null) return false;

            entry.ExtractToFile(WintunPath, overwrite: true);
            try { File.Delete(zipPath); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
