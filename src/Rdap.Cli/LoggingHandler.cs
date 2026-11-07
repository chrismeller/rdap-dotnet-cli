using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Rdap.Diagnostics;

namespace Rdap.Cli;

/// <summary>Logs each HTTP request: URL, proxy, status, timing, and (at trace level) headers.</summary>
internal sealed class LoggingHandler : DelegatingHandler
{
    private readonly IRdapLogger _log;
    private readonly IWebProxy? _proxy;
    private int _counter;

    /// <param name="proxy">The proxy the inner handler will use, so it can be reported per request; null means none.</param>
    public LoggingHandler(HttpMessageHandler inner, IRdapLogger log, IWebProxy? proxy)
        : base(inner)
    {
        _log = log;
        _proxy = proxy;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _counter);
        var uri = request.RequestUri!;
        _log.Log(RdapLogLevel.Info, $"http#{id}: {request.Method} {uri}");

        if (_log.IsEnabled(RdapLogLevel.Debug))
        {
            _log.Log(RdapLogLevel.Debug, $"http#{id}: {DescribeProxy(uri)}");
        }

        if (_log.IsEnabled(RdapLogLevel.Trace))
        {
            LogHeaders(id, ">", request.Headers);
        }

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            _log.Log(RdapLogLevel.Info, $"http#{id}: cancelled after {sw.ElapsedMilliseconds} ms (timeout or Ctrl+C) while waiting for {uri.Host}", ex);
            throw;
        }
        catch (Exception ex)
        {
            _log.Log(RdapLogLevel.Info, $"http#{id}: failed after {sw.ElapsedMilliseconds} ms", ex);
            throw;
        }

        var length = response.Content.Headers.ContentLength is { } l ? $", {l:N0} bytes" : "";
        var type = response.Content.Headers.ContentType is { } t ? $", {t.MediaType}" : "";
        _log.Log(RdapLogLevel.Info, $"http#{id}: {(int)response.StatusCode} {response.ReasonPhrase} in {sw.ElapsedMilliseconds} ms (HTTP/{response.Version}{type}{length})");
        if (response.RequestMessage?.RequestUri is { } final && final != uri)
        {
            _log.Log(RdapLogLevel.Info, $"http#{id}: redirected to {final}");
        }

        if (_log.IsEnabled(RdapLogLevel.Trace))
        {
            LogHeaders(id, "<", response.Headers);
            LogHeaders(id, "<", response.Content.Headers);
        }

        return response;
    }

    private string DescribeProxy(Uri uri)
    {
        if (_proxy is null)
        {
            return "proxy: none";
        }

        try
        {
            if (_proxy.IsBypassed(uri))
            {
                return "proxy: none for this host (direct connection)";
            }

            var target = _proxy.GetProxy(uri);
            return target is null || target == uri ? "proxy: none (direct connection)" : $"proxy: via {EnvironmentReport.Redact(target)}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"proxy: could not evaluate ({ex.Message})";
        }
    }

    private void LogHeaders(int id, string direction, HttpHeaders headers)
    {
        foreach (var (name, values) in headers)
        {
            var value = name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                ? "<redacted>"
                : string.Join(name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || name.Equals("Server", StringComparison.OrdinalIgnoreCase) ? " " : ", ", values);
            _log.Log(RdapLogLevel.Trace, $"http#{id}: {direction} {name}: {value}");
        }
    }
}
