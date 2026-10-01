using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

/// <summary>
/// 지연 측정 파라미터의 통합 읽기 진입점. 프록시 페이지와 미니 패널이 같은 설정 공유,
/// 여기저기 URL / 시간 초과 / 동시성 하드코딩 방지.
/// </summary>
public static class DelayTestSettings
{
    public const string DefaultUrl = "https://www.gstatic.com/generate_204";
    public const int DefaultTimeoutMs = 3000;
    // 동시성이 너무 높으면 노드끼리 대역폭/연결을 다투어 일괄 측정 수치가 부풀려짐. 기본 8로 낮추면 단일 측정에 가까워짐.
    public const int DefaultConcurrency = 8;

    public static async Task<(string Url, int TimeoutMs, int Concurrency)> ReadAsync(ISettingsService settings)
    {
        var url = await settings.GetAsync("core.delayTestUrl").ConfigureAwait(false);
        var timeout = await settings.GetIntAsync("core.delayTestTimeout", DefaultTimeoutMs).ConfigureAwait(false);
        var concurrency = await settings.GetIntAsync("core.delayTestConcurrency", DefaultConcurrency).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(url)) url = DefaultUrl;
        if (timeout <= 0) timeout = DefaultTimeoutMs;
        if (concurrency <= 0) concurrency = DefaultConcurrency;

        return (url!, timeout, concurrency);
    }
}
