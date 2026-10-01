using System.Net;
using System.Text;

namespace Resenz.App.ViewModels;

/// <summary>
/// 한 연결의 핵심 필드를 바로 붙여넣을 수 있는 Mihomo 규칙 텍스트로 조립,
/// 연결 상세 팝업용"한 번에 규칙으로 복사"사용.
/// </summary>
public static class RuleTextBuilder
{
    public static string Build(ConnectionRowViewModel row)
    {
        var sb = new StringBuilder();
        var (host, ip, port) = SplitHostPort(row);

        if (IsIpAddress(ip))
        {
            sb.AppendLine(IsIpV6(ip)
                ? $"IP-CIDR6,{ip}/128"
                : $"IP-CIDR,{ip}/32");
        }
        else if (!string.IsNullOrEmpty(host))
        {
            // 도메인은 정확한 도메인과 접미사 두 가지를 우선 제시해 바로 고르기 쉽게 함.
            sb.AppendLine($"DOMAIN,{host}");
            sb.AppendLine($"DOMAIN-SUFFIX,{host}");
        }

        if (port > 0)
            sb.AppendLine($"DST-PORT,{port}");

        if (!string.IsNullOrEmpty(row.Process))
            sb.AppendLine($"PROCESS-NAME,{row.Process}");

        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? "(이 연결에서 사용 가능한 규칙 필드를 뽑을 수 없음)" : text;
    }

    private static (string Host, string Ip, int Port) SplitHostPort(ConnectionRowViewModel row)
    {
        // Host는 "example.com" 또는 "1.2.3.4:8080" 형태일 수 있음. Host를 우선 해석하고 실패하면 Destination으로 폴백.
        var raw = row.Host;
        var (host, port) = SplitHostPortString(raw);
        if (string.IsNullOrEmpty(host))
        {
            var dst = SplitHostPortString(row.Destination);
            host = dst.Host;
            if (port <= 0) port = dst.Port;
        }
        return (host, IsIpAddress(host) ? host : row.Destination.Split(':')[0], port);
    }

    private static (string Host, int Port) SplitHostPortString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ("", 0);
        var colon = value.LastIndexOf(':');
        if (colon <= 0) return (value, 0);
        var host = value[..colon];
        if (int.TryParse(value[(colon + 1)..], out var port)) return (host, port);
        return (value, 0);
    }

    private static bool IsIpAddress(string? value)
        => value is not null && IPAddress.TryParse(value, out _);

    private static bool IsIpV6(string value)
        => value.Contains(':');
}
