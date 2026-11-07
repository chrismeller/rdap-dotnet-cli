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
                log.Log(RdapLogLevel.Debug, $"env: {name}={(name.Contains("PROXY", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var u) ? Redact(u) : value)}");
            }
        }

        if (!any)
        {
            log.Log(RdapLogLevel.Debug, "env: no proxy or certificate environment variables set");
        }
    }

    /// <summary>Hides credentials in a proxy URL.</summary>
    public static string Redact(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) ? uri.ToString() : uri.ToString().Replace(uri.UserInfo + "@", "***@", StringComparison.Ordinal);
}
