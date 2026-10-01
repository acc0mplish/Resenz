using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Networking.Connectivity;

namespace Resenz.Core.Services;

/// <summary>
/// SSID 인지: 현재 WiFi 네트워크 변화를 수신 대기하며 SSID를 구독/일시중지 규칙과 매칭,
/// 구독 자동 전환 또는 시스템 프록시 일시중지. 용도:"집에서 직접 연결 / 회사에서 자동 프록시"등 상황.
/// </summary>
public sealed class SsidService : IDisposable
{
    private readonly ResenzHost _host;
    private string? _lastSsid;
    private bool _proxyPausedBySsid;

    public SsidService(ResenzHost host)
    {
        _host = host;
    }

    /// <summary>수신 대기 시작: 즉시 한 번 검사한 뒤 WiFi 변화 때 검사.</summary>
    public void Start()
    {
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _ = CheckAsync();
    }

    public void Dispose()
    {
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
    }

    private void OnNetworkStatusChanged(object? sender)
        => _ = CheckAsync();

    /// <summary>현재 SSID 읽기(WiFi 없음 또는 획득 실패 시 null 반환).</summary>
    public static string? GetCurrentSsid()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)) continue;
                var match = Regex.Match(trimmed, @"SSID\s*:\s*(.+)");
                if (!match.Success) continue;
                var ssid = match.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(ssid) ? null : ssid;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task CheckAsync()
    {
        try
        {
            var ssid = await Task.Run(GetCurrentSsid).ConfigureAwait(false);
            if (string.Equals(ssid, _lastSsid, StringComparison.OrdinalIgnoreCase)) return;
            _lastSsid = ssid;
            await ApplyRulesAsync(ssid).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ApplyRulesAsync(string? ssid)
    {
        // 프록시 일시중지 규칙: 적중한 SSID에서 시스템 프록시를 끄고 떠나면 복원.
        var pauseSsids = (await _host.Settings.GetAsync("ui.pauseSsids").ConfigureAwait(false) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inPauseList = ssid is not null && pauseSsids.Contains(ssid, StringComparer.OrdinalIgnoreCase);

        if (inPauseList)
        {
            if (!_proxyPausedBySsid && _host.SystemProxy.IsEnabled())
            {
                _host.SystemProxy.Disable();
                _proxyPausedBySsid = true;
            }
        }
        else if (_proxyPausedBySsid)
        {
            // 일시중지 네트워크 이탈: 이 서비스가 껐던 시스템 프록시를 복원.
            _host.SystemProxy.Enable("127.0.0.1:" + EffectiveProxyPort(), DefaultBypass);
            _proxyPausedBySsid = false;
        }

        // 구독 전환 규칙: 적중한 SSID에서 지정 구독으로 자동 전환.
        if (ssid is not null)
        {
            var mapLines = (await _host.Settings.GetAsync("ui.ssidProfileMap").ConfigureAwait(false) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var entry = mapLines
                .Select(l => l.Split('\t', 2))
                .FirstOrDefault(parts => parts.Length == 2
                    && string.Equals(parts[0].Trim(), ssid, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                var profileName = entry[1].Trim();
                var items = await _host.Profiles.ListAsync().ConfigureAwait(false);
                var target = items.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                if (target is not null && !target.IsActive)
                {
                    await _host.Profiles.SetActiveAsync(target.Id).ConfigureAwait(false);
                    await _host.ApplyActiveProfileAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static int EffectiveProxyPort()
    {
        try
        {
            return DownloadProxy.EffectivePort;
        }
        catch
        {
            return 7890;
        }
    }

    private const string DefaultBypass = "localhost;127.*;10.*;172.16.*;192.168.*;<local>";
}
