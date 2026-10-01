using YamlDotNet.RepresentationModel;

namespace Resenz.Core.Services;

public sealed record RuntimeYamlOptions(
    int MixedPort,
    int ControllerPort,
    string Secret,
    string Mode,
    string LogLevel,
    bool AllowLan,
    bool Ipv6,
    bool TunEnabled,
    string TunStack = "mixed",
    bool TunAutoRoute = true,
    bool TunAutoRedirect = false,
    bool TunAutoDetectInterface = true,
    bool TunStrictRoute = false,
    int TunMtu = 0,
    IReadOnlyList<string>? TunDnsHijack = null,
    IReadOnlyList<string>? TunRouteExcludeAddress = null);

public static class MihomoConfigBuilder
{
    public static string BuildRuntimeYaml(string subscriptionYaml, RuntimeYamlOptions options)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(subscriptionYaml);
        yaml.Load(reader);

        var root = yaml.Documents.Count > 0
            ? yaml.Documents[0].RootNode as YamlMappingNode
            : null;

        if (root is null)
        {
            root = new YamlMappingNode();
            yaml.Documents.Clear();
            yaml.Documents.Add(new YamlDocument(root));
        }

        var keysToStrip = new[]
        {
            "external-controller", "secret", "port", "socks-port", "mixed-port",
            "allow-lan", "mode", "log-level", "ipv6", "tun", "external-controller-cors",
            "dns",
        };
        foreach (var key in keysToStrip)
            root.Children.Remove(new YamlScalarNode(key));

        root.Children[new YamlScalarNode("mixed-port")] = new YamlScalarNode(options.MixedPort.ToString());
        root.Children[new YamlScalarNode("allow-lan")] = new YamlScalarNode(options.AllowLan ? "true" : "false");
        root.Children[new YamlScalarNode("mode")] = new YamlScalarNode(options.Mode);
        root.Children[new YamlScalarNode("log-level")] = new YamlScalarNode(options.LogLevel);
        root.Children[new YamlScalarNode("ipv6")] = new YamlScalarNode(options.Ipv6 ? "true" : "false");
        root.Children[new YamlScalarNode("external-controller")] = new YamlScalarNode($"127.0.0.1:{options.ControllerPort}");
        root.Children[new YamlScalarNode("secret")] = new YamlScalarNode(options.Secret);

        root.Children[new YamlScalarNode("dns")] = BuildDnsNode(options.TunEnabled);

        var cors = new YamlMappingNode
        {
            { "allow-origins", new YamlSequenceNode(new YamlScalarNode("*")) },
            { "allow-private-network", new YamlScalarNode("true") },
        };
        root.Children[new YamlScalarNode("external-controller-cors")] = cors;

        if (options.TunEnabled)
        {
            var tun = new YamlMappingNode
            {
                { "enable", new YamlScalarNode("true") },
                { "stack", new YamlScalarNode(string.IsNullOrWhiteSpace(options.TunStack) ? "mixed" : options.TunStack) },
                { "auto-route", new YamlScalarNode(options.TunAutoRoute ? "true" : "false") },
                { "auto-redirect", new YamlScalarNode(options.TunAutoRedirect ? "true" : "false") },
                { "auto-detect-interface", new YamlScalarNode(options.TunAutoDetectInterface ? "true" : "false") },
            };

            if (options.TunStrictRoute)
                tun.Add("strict-route", new YamlScalarNode("true"));

            if (options.TunMtu is > 0)
                tun.Add("mtu", new YamlScalarNode(options.TunMtu.ToString()));

            var dnsHijack = options.TunDnsHijack is { Count: > 0 }
                ? options.TunDnsHijack
                : new[] { "any:53" };
            tun.Add("dns-hijack", new YamlSequenceNode(dnsHijack.Select(v => new YamlScalarNode(v))));

            if (options.TunRouteExcludeAddress is { Count: > 0 })
                tun.Add("route-exclude-address", new YamlSequenceNode(options.TunRouteExcludeAddress.Select(v => new YamlScalarNode(v))));

            root.Children[new YamlScalarNode("tun")] = tun;
        }
        else
        {
            root.Children.Remove(new YamlScalarNode("tun"));
        }

        using var writer = new StringWriter();
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    /// <summary>
    /// TUN 모드에서는 dns-hijack이 DNS 쿼리를 코어에 넘기므로 dns 섹션을 함께 제공해야 하며,
    /// 그렇지 않으면 도메인 해석이 안 됨. 일반 모드는 DNS 누출을 막고 도메인 규칙을 지원하려고 fake-ip를 사용.
    /// </summary>
    private static YamlMappingNode BuildDnsNode(bool tunEnabled)
    {
        // 어디서든 안정적으로 접근되는 공용 DoH. 프록시 서버 도메인(CDN 도메인 등)의
        // 해석이 지연되어 노드 연결이 실패하는 일이 없도록 응답이 빠른 서버를 우선 배치.
        var nameservers = new YamlSequenceNode(
            new YamlScalarNode("https://1.1.1.1/dns-query"),
            new YamlScalarNode("https://8.8.8.8/dns-query"));

        // 프록시 서버 도메인은 프록시를 거치지 않고 직접 해석돼야 함. 그러지 않으면 노드 연결 수립 불가.
        var proxyNameservers = new YamlSequenceNode(
            new YamlScalarNode("1.1.1.1"),
            new YamlScalarNode("8.8.8.8"));

        var dns = new YamlMappingNode
        {
            { "enable", new YamlScalarNode("true") },
            { "ipv6", new YamlScalarNode(tunEnabled ? "true" : "false") },
            { "enhanced-mode", new YamlScalarNode("fake-ip") },
            { "fake-ip-range", new YamlScalarNode("198.18.0.1/16") },
            { "fake-ip-filter", new YamlSequenceNode(
                new YamlScalarNode("*.lan"),
                new YamlScalarNode("*.local"),
                new YamlScalarNode("*.msftconnecttest.com")) },
            { "default-nameserver", new YamlSequenceNode(
                new YamlScalarNode("1.1.1.1"),
                new YamlScalarNode("8.8.8.8")) },
            { "nameserver", nameservers },
            { "proxy-server-nameserver", proxyNameservers },
        };

        return dns;
    }

    public static string BuildFallbackYaml(RuntimeYamlOptions options)
    {
        return BuildRuntimeYaml("""
            proxies: []
            proxy-groups: []
            rules:
              - MATCH,DIRECT
            """, options);
    }

    /// <summary>
    /// runtime 설정에서 proxy-groups 선언 순서를 읽음.
    /// mihomo 의 /proxies는 JSON 객체를 반환하며 키 순서가 불안정하므로 이를 기준으로 설정 순서를 복원해야 함.
    /// </summary>
    public static IReadOnlyList<string> ReadProxyGroupOrder(string yamlPath)
    {
        try
        {
            if (!File.Exists(yamlPath)) return Array.Empty<string>();

            var yaml = new YamlStream();
            using var reader = new StreamReader(yamlPath);
            yaml.Load(reader);

            if (yaml.Documents.Count == 0) return Array.Empty<string>();
            if (yaml.Documents[0].RootNode is not YamlMappingNode root) return Array.Empty<string>();
            if (!root.Children.TryGetValue(new YamlScalarNode("proxy-groups"), out var node)) return Array.Empty<string>();
            if (node is not YamlSequenceNode groups) return Array.Empty<string>();

            var order = new List<string>();
            foreach (var item in groups.Children)
            {
                if (item is not YamlMappingNode group) continue;
                if (!group.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode)) continue;
                if (nameNode is YamlScalarNode scalar && scalar.Value is { Length: > 0 } name)
                    order.Add(name);
            }
            return order;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
