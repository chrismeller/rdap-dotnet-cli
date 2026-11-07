using System.Net.Sockets;
using System.Runtime.InteropServices;
using Rdap.Diagnostics;

namespace Rdap.Cli;

/// <summary>The environment facts that most often explain a network failure, logged once at <c>-vv</c>.</summary>
internal static class EnvironmentReport
{
    // Variables .NET's default proxy (and OpenSSL) read on Linux/macOS; Windows uses the system proxy settings too.
    private static readonly string[] Variables =
    [
        "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "DOTNET_SYSTEM_NET_DISABLEIPV6", "DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER",
    ];

    public static void Log(IRdapLogger log, string cacheDirectory, TimeSpan timeout)
    {
        if (!log.IsEnabled(RdapLogLevel.Debug))
        {
            return;
        }

        log.Log(RdapLogLevel.Debug, $"env: rdap {CliApp.Version} ({Environment.ProcessPath}), {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}");
        log.Log(RdapLogLevel.Debug, $"env: OS {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        log.Log(RdapLogLevel.Debug, $"env: IPv4 {(Socket.OSSupportsIPv4 ? "available" : "unavailable")}, IPv6 {(Socket.OSSupportsIPv6 ? "available" : "unavailable")}");
        log.Log(RdapLogLevel.Debug, $"env: timeout {timeout.TotalSeconds:0}s per request, bootstrap cache {cacheDirectory}");

        var any = false;
        foreach (var name in Variables)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                any = true;
                log.Log(RdapLogLevel.Debug, $"env: {name}={(name.Contains("PROXY", StringComparison.OrdinalIgnoreCase) ? RedactProxyValue(value) : value)}");
            }
        }

        if (!any)
        {
            log.Log(RdapLogLevel.Debug, "env: no proxy or certificate environment variables set");
        }
    }

    /// <summary>
    /// Hides credentials in a proxy setting, with or without a scheme (<c>user:pass@proxy:3128</c> is common),
    /// and in each entry of a comma-separated list.
    /// </summary>
    internal static string RedactProxyValue(string value) =>
        string.Join(",", value.Split(',').Select(entry =>
        {
            var at = entry.LastIndexOf('@');
            if (at < 0)
            {
                return entry;
            }

            var scheme = entry.IndexOf("://", StringComparison.Ordinal);
            var start = scheme >= 0 && scheme < at ? scheme + 3 : 0;
            return entry[..start] + "***" + entry[at..];
        }));

    /// <summary>Hides credentials in a proxy URL.</summary>
    public static string Redact(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) ? uri.ToString() : uri.ToString().Replace(uri.UserInfo + "@", "***@", StringComparison.Ordinal);
}
