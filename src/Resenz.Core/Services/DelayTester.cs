namespace Resenz.Core.Services;

using System.Diagnostics;
using System.Net.Http;

/// <summary>
/// 통합 지연 측정: 동시성 제어, 여러 번 중 최솟값.
/// 동시 경쟁은 한 번 측정값을 부풀릴 뿐이므로 최솟값이 경쟁 없는 상태의 실제 지연에 가장 가까움.
/// 노드 지연 측정(코어 API 경유)과 네트워크 대상 측정(HTTP 경유)은 같은 횟수·동시성 정책을 공유해 동작 일관성 유지.
/// </summary>
public static class DelayTester
{
    /// <summary>대상마다 측정 횟수.</summary>
    public const int Attempts = 3;

    /// <summary>처음으로 이 임계값 이하의 유효 지연이 측정되면 조기 종료해 불필요한 재측정을 피함.</summary>
    private const int EarlyExitMs = 200;

    /// <summary>노드 하나를 측정, 지연 ms 반환. -1은 전부 실패.</summary>
    public static async Task<int> MeasureAsync(
        IMihomoApiClient api, string name, string url, int timeoutMs, CancellationToken ct = default)
    {
        var best = -1;
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                var delay = await api.ProxyDelayAsync(name, url, timeoutMs, ct).ConfigureAwait(false);
                if (delay > 0 && (best < 0 || delay < best)) best = delay;
                if (i == 0 && best > 0 && best < EarlyExitMs) break;
            }
            catch (OperationCanceledException)
            {
                return best;
            }
            catch
            {
            }
        }
        return best;
    }

    /// <summary>
    /// HTTP 대상 하나를 측정(여러 번 중 최솟값), 지연 ms 반환. null은 전부 실패.
    /// 노드 측정과 동일 정책: 연결 수립 후 요청 헤더 수신 즉시 반환, 같은 조기 종료 임계값 사용.
    /// </summary>
    public static async Task<long?> MeasureHttpAsync(
        Func<HttpClient> clientFactory, string url, int timeoutMs = 6000, CancellationToken ct = default)
    {
        long? best = null;
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                using var client = clientFactory();
                var sw = Stopwatch.StartNew();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                sw.Stop();
                var ms = sw.ElapsedMilliseconds;
                if (best is null || ms < best) best = ms;
                if (i == 0 && ms < EarlyExitMs) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return best;
            }
            catch
            {
            }
        }
        return best;
    }

    /// <summary>
    /// 노드 그룹을 동시 측정하며 각각 완료 시 onMeasured(name 콜백, delay).
    /// 동시성 상한은 concurrency가 제어하며 콜백은 호출자 컨텍스트(보통 스레드 풀)에서 실행.
    /// </summary>
    public static async Task MeasureAllAsync(
        IMihomoApiClient api,
        IReadOnlyCollection<string> names,
        string url,
        int timeoutMs,
        int concurrency,
        Func<string, int, Task> onMeasured,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = names.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = await MeasureAsync(api, name, url, timeoutMs, ct).ConfigureAwait(false);
                await onMeasured(name, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// HTTP 대상 그룹을 동시 측정하며 각각 완료 시 onMeasured(target 콜백, delayMs).
    /// 노드 측정과 동일 정책: 동시성 제어 + 대상마다 여러 번 중 최솟값.
    /// </summary>
    public static async Task MeasureAllHttpAsync<T>(
        IReadOnlyCollection<T> targets,
        Func<T, string> urlSelector,
        Func<HttpClient> clientFactory,
        int concurrency,
        int timeoutMs,
        Func<T, long?, Task> onMeasured,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = await MeasureHttpAsync(clientFactory, urlSelector(target), timeoutMs, ct).ConfigureAwait(false);
                await onMeasured(target, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

