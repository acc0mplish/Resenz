using System.Net;

namespace Resenz.Core.Services;

/// <summary>
/// 다운로드 프록시: 코어 실행 중일 때 코어/geodata/구독 등 다운로드 요청을 로컬 혼합 포트로 전달,
/// 자체 프록시로 GitHub 등 제한 자원 접근.
/// 우선순위: 수동 설정 다운로드 프록시 포트 &gt; 코어 혼합 포트 &gt; 시스템 프록시 &gt; 직접 연결.
/// </summary>
public static class DownloadProxy
{
    private static volatile int _corePort;
    private static volatile int _manualPort;

    /// <summary>코어 실행 중일 때 혼합 포트를 설정. 0은 코어 미실행.</summary>
    public static void SetCorePort(int port) => _corePort = port is > 0 and < 65536 ? port : 0;

    /// <summary>사용자가 수동으로 지정한 다운로드 프록시 포트. 0은 미설정.</summary>
    public static void SetManualPort(int port) => _manualPort = port is > 0 and < 65536 ? port : 0;

    /// <summary>현재 적용 중인 다운로드 프록시 포트. 0은 시스템 프록시 또는 직접 연결.</summary>
    public static int EffectivePort
    {
        get
        {
            var m = _manualPort;
            if (m > 0) return m;
            var c = _corePort;
            return c > 0 ? c : 0;
        }
    }

    public static IWebProxy Create() => new Provider();

    private sealed class Provider : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            // 로컬 루프백 주소는 절대 프록시하지 않아 자기 자신을 프록시하는 무한 루프 방지.
            if (destination.IsLoopback) return destination;

            var port = EffectivePort;
            if (port > 0) return new Uri($"http://127.0.0.1:{port}");

            // 전용 프록시 미설정: 시스템 프록시 사용(사용자가 켠 경우).
            try
            {
                var sys = WebRequest.DefaultWebProxy;
                if (sys is not null)
                {
                    var p = sys.GetProxy(destination);
                    if (p is not null && p != destination) return p;
                }
            }
            catch
            {
            }

            return destination;
        }

        public bool IsBypassed(Uri host)
        {
            if (host.IsLoopback) return true;
            return EffectivePort <= 0;
        }
    }
}
