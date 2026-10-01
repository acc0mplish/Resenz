using System.Net;
using System.Text;

namespace Resenz.Core.Services;

/// <summary>
/// 로컬 PAC 서버: 무작위 루프백 포트에서 수신 대기하며 로컬 혼합 포트를 가리키는 PAC 스크립트를 반환,
/// 시스템 프록시 PAC 모드에서 사용. 고정 127.0.0.1보다 안정적(포트 무작위, 점유 충돌 없음).
/// </summary>
public sealed class PacServer : IDisposable
{
    private HttpListener? _listener;
    private string? _pacText;
    private readonly object _gate = new();
    private volatile bool _running;

    /// <summary>현재 수신 포트. 미기동 시 0.</summary>
    public int Port { get; private set; }

    /// <summary>PAC 스크립트 주소. 미기동 시 null.</summary>
    public string? PacUrl { get; private set; }

    /// <summary>수신 대기 시작. mixedPort는 코어 혼합 포트(HTTP/SOCKS5 모두 이 포트).</summary>
    public bool Start(int mixedPort)
    {
        Stop();
        try
        {
            _pacText = BuildPacScript(mixedPort);
            // 여러 무작위 루프백 포트를 시도해 우연한 점유 회피.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                var prefix = $"http://127.0.0.1:{port}/";
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add(prefix);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    PacUrl = prefix + "proxy.pac";
                    _running = true;
                    _ = Task.Run(ListenLoop);
                    return true;
                }
                catch (HttpListenerException)
                {
                    _listener?.Close();
                    _listener = null;
                }
            }
            return false;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    private async Task ListenLoop()
    {
        while (_running && _listener?.IsListening == true)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            try
            {
                var pac = _pacText;
                var bytes = Encoding.UTF8.GetBytes(pac ?? "");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ns-proxy-autoconfig";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch
            {
                ctx?.Response.Abort();
            }
        }
    }

    private static string BuildPacScript(int mixedPort)
        => $$"""
            function FindProxyForURL(url, host) {
                if (isPlainHostName(host) || dnsDomainIs(host, "localhost")) return "DIRECT";
                return "PROXY 127.0.0.1:{{mixedPort}}; SOCKS5 127.0.0.1:{{mixedPort}}; DIRECT";
            }
            """;

    public void Stop()
    {
        _running = false;
        lock (_gate)
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
        }
        Port = 0;
        PacUrl = null;
        _pacText = null;
    }

    public void Dispose() => Stop();
}
