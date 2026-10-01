using Resenz.Core.Services;

namespace Resenz.App;

public static class AppHost
{
    private static ResenzHost? _host;

    public static ResenzHost Host => _host ?? throw new InvalidOperationException("AppHost 아직 초기화 안 됨");

    public static bool IsReady => _host is not null;

    public static async Task<ResenzHost> InitializeAsync()
    {
        if (_host is not null) return _host;
        var host = new ResenzHost();
        await host.InitializeAsync().ConfigureAwait(false);
        // Core 층의 설정 리로드 이벤트를 App 층 전역 브로드캐스트로 연결해 각 뷰가 구독하게 함.
        host.ProxiesReloaded += (_, _) => ViewModels.AppSignals.RaiseProxiesChanged();
        _host = host;
        return host;
    }

    public static void Shutdown()
    {
        _host?.Dispose();
        _host = null;
    }
}
