using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

public sealed partial class LatencyTargetViewModel : ObservableObject
{
    public string Name { get; }
    public string Url { get; }

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "—";

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    /// <summary>지연 ms. -1=시간 초과/실패, null=미측정.</summary>
    public long? LatencyMs { get; private set; }

    public LatencyTargetViewModel(string name, string url)
    {
        Name = name;
        Url = url;
    }

    public void SetResult(long? ms)
    {
        LatencyMs = ms;
        LatencyText = ms is null ? "시간 초과" : $"{ms} ms";
    }
}

public sealed partial class NetworkViewModel : ObservableObject
{
    private readonly ResenzHost _host;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<LatencyTargetViewModel> Targets { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "미검출";

    [ObservableProperty]
    public partial string ExitIp { get; set; } = "—";

    [ObservableProperty]
    public partial string ExitCountry { get; set; } = "";

    /// <summary>출구 IP 소재지 국기 emoji(빈 값은 없음).</summary>
    [ObservableProperty]
    public partial string ExitFlag { get; set; } = "";

    [ObservableProperty]
    public partial bool HasExitIp { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingExit { get; set; }

    [ObservableProperty]
    public partial bool IsTestingAll { get; set; }

    public NetworkViewModel(ResenzHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        Targets.Add(new LatencyTargetViewModel("Google", "https://www.gstatic.com/generate_204"));
        Targets.Add(new LatencyTargetViewModel("Cloudflare", "https://cloudflare.com/cdn-cgi/trace"));
        Targets.Add(new LatencyTargetViewModel("GitHub", "https://github.com"));
        Targets.Add(new LatencyTargetViewModel("YouTube", "https://www.youtube.com"));
        Targets.Add(new LatencyTargetViewModel("Wikipedia", "https://www.wikipedia.org"));
    }

    public async Task RefreshAsync()
    {
        StatusText = "검출 중…";
        await CheckExitIpAsync();
        await TestAllAsync();
    }

    /// <summary>현재 프록시를 경유해 출구 IP와 소재지 획득(다중 소스 폴백). 코어 실행 중에만 검출해 실제 프록시 출구임을 보장.</summary>
    [RelayCommand]
    public async Task CheckExitIpAsync()
    {
        var core = global::Resenz.App.AppHost.Host.Core;
        if (IsCheckingExit || core.Api is null || core.State != Resenz.Core.Services.CoreState.Running) return;
        IsCheckingExit = true;
        try
        {
            var ip = await QueryExitIpAsync();
            var code = await QueryCountryCodeAsync(ip);
            _dispatcher.TryEnqueue(() =>
            {
                ExitIp = ip;
                var country = CountryNames.GetName(code);
                ExitCountry = country.Length == 0 ? "" : country;
                ExitFlag = CountryNames.GetFlag(code);
                HasExitIp = true;
                StatusText = "검출 완료";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"출구 검출 실패: {{ex.Message}");
        }
        finally
        {
            // 백그라운드 스레드일 수 있으므로 바인딩 속성 수정은 UI 스레드로 디스패치 필수.
            _dispatcher.TryEnqueue(() => IsCheckingExit = false);
        }
    }

    /// <summary>국가 코드 조회: 외부 geo 서비스로 직접 조회(코어 geoip 인터페이스를 쓰면 404를 하얗게 기다리게 되므로 회피).</summary>
    private async Task<string> QueryCountryCodeAsync(string ip)
    {
        var (_, _, externalCode) = await QueryGeoAsync(ip).ConfigureAwait(false);
        return externalCode;
    }

    private async Task<string> QueryExitIpAsync()
    {
        using var client = MakeClient();
        foreach (var url in new[] { "https://api.ipify.org", "https://ipv4.icanhazip.com", "https://ifconfig.me/ip" })
        {
            try
            {
                var text = (await client.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch
            {
            }
        }
        throw new InvalidOperationException("모든 IP 소스 접근 불가");
    }

    private async Task<(string Country, string Region, string CountryCode)> QueryGeoAsync(string ip)
    {
        try
        {
            using var client = MakeClient();
            var json = await client.GetStringAsync($"https://ipwho.is/{ip}").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.GetBoolean() == false) return ("", "", "");
            var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
            var region = root.TryGetProperty("region", out var r) ? r.GetString() : null;
            var code = root.TryGetProperty("country_code", out var cc) ? cc.GetString() : null;
            return (country ?? "", region ?? "", code ?? "");
        }
        catch
        {
            return ("", "", "");
        }
    }

    /// <summary>모든 대상 지연을 동시 측정. 노드 측정과 동일 정책.</summary>
    [RelayCommand]
    public async Task TestAllAsync()
    {
        if (IsTestingAll) return;
        IsTestingAll = true;
        try
        {
            foreach (var target in Targets)
                target.IsTesting = true;

            // 노드 측정과 동일 정책: 동시성 제어 + 대상마다 여러 번 중 최솟값.
            await DelayTester.MeasureAllHttpAsync(
                Targets,
                t => t.Url,
                MakeLatencyClient,
                concurrency: 4,
                timeoutMs: 6000,
                (target, ms) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        target.SetResult(ms);
                        target.IsTesting = false;
                    });
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

            _dispatcher.TryEnqueue(() => StatusText = "검출 완료");
        }
        finally
        {
            // 백그라운드 스레드일 수 있으므로(ConfigureAwait(false) 이후) 바인딩 속성 수정은 UI 스레드로 다시 디스패치해야 함.
            _dispatcher.TryEnqueue(() => IsTestingAll = false);
        }
    }

    private HttpClient MakeClient()
    {
        var handler = new HttpClientHandler { Proxy = DownloadProxy.Create() };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>지연 측정 클라이언트: 현재 프록시 경유(출구 IP 검출과 같은 경로)로 엔드투엔드 실제 지연을 측정.</summary>
    private static HttpClient MakeLatencyClient()
    {
        var handler = new HttpClientHandler { Proxy = DownloadProxy.Create() };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }
}
