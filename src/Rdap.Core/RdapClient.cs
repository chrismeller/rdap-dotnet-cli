using System.Net;
using System.Net.Http.Headers;
using Rdap.Bootstrap;
using Rdap.Diagnostics;
using Rdap.Models;
using Rdap.Serialization;

namespace Rdap;

/// <summary>One RDAP response: the raw JSON, and the typed model if it parsed.</summary>
/// <param name="RequestUri">The URL that was queried.</param>
/// <param name="Json">The raw response body.</param>
/// <param name="Data">The parsed object, or null if parsing failed (see <paramref name="ParseError"/>).</param>
/// <param name="ParseError">Why parsing failed, if it did.</param>
public sealed record RdapResult(Uri RequestUri, string Json, RdapObject? Data, RdapParseException? ParseError);

public sealed class RdapClient
{
    private readonly HttpClient _http;
    private readonly BootstrapService _bootstrap;

    public RdapClient(HttpClient http, BootstrapService bootstrap)
    {
        _http = http;
        _bootstrap = bootstrap;
    }

    /// <summary>Receives diagnostic detail (server resolution, response parsing).</summary>
    public IRdapLogger Logger { get; set; } = NullRdapLogger.Instance;

    /// <summary>Resolves the base URL of the authoritative server for a query via IANA bootstrap.</summary>
    /// <exception cref="RdapNoServerException">No server is registered for the query.</exception>
    public async Task<Uri> ResolveServerAsync(RdapQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        Uri? server;
        switch (query.Type)
        {
            case QueryType.Domain:
            case QueryType.Nameserver:
                server = (await _bootstrap.GetRegistryAsync([BootstrapKind.Dns], cancellationToken)).FindDomainServer(query.Value);
                break;
            case QueryType.Ip:
                var kind = query.Address!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? BootstrapKind.Ipv4 : BootstrapKind.Ipv6;
                server = (await _bootstrap.GetRegistryAsync([kind], cancellationToken)).FindIpServer(query.Address);
                break;
            case QueryType.Autnum:
                server = (await _bootstrap.GetRegistryAsync([BootstrapKind.Asn], cancellationToken)).FindAsnServer(query.AsNumber!.Value);
                break;
            case QueryType.Entity:
                server = (await _bootstrap.GetRegistryAsync([BootstrapKind.ObjectTags], cancellationToken)).FindEntityServer(query.Value);
                if (server is null)
                {
                    throw new RdapNoServerException(
                        $"Can't tell which registry '{query.Value}' belongs to. Entity handles need a registry suffix " +
                        "(e.g. XXXX-ARIN) or an explicit --server.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query));
        }

        if (server is not null)
        {
            Logger.Log(RdapLogLevel.Info, $"bootstrap: {query.Type} '{query.Value}' -> {server}");
        }

        return server ?? throw new RdapNoServerException(query.Type switch
        {
            QueryType.Domain or QueryType.Nameserver => $"No RDAP server is registered with IANA for '{query.Value}' (the TLD may not support RDAP).",
            _ => $"No RDAP server is registered with IANA for '{query.Value}'.",
        });
    }

    /// <summary>Looks up a query against an explicit base URL, or the bootstrap-resolved one.</summary>
    public async Task<RdapResult> QueryAsync(RdapQuery query, Uri? server = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (server is null)
        {
            server = await ResolveServerAsync(query, cancellationToken);
        }
        else
        {
            Logger.Log(RdapLogLevel.Info, $"using --server {server} (skipping IANA bootstrap)");
        }

        if (!server.AbsoluteUri.EndsWith('/'))
        {
            server = new Uri(server.AbsoluteUri + "/");
        }

        return await GetAsync(new Uri(server, query.Path), cancellationToken);
    }

    /// <summary>Fetches and parses an RDAP URL.</summary>
    /// <exception cref="RdapHttpException">The server returned a non-success status.</exception>
    public async Task<RdapResult> GetAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rdap+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var finalUri = response.RequestMessage?.RequestUri ?? uri;

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpError(response, body, finalUri);
        }

        try
        {
            return new RdapResult(finalUri, body, RdapResponseParser.Parse(body), null);
        }
        catch (RdapParseException ex)
        {
            Logger.Log(RdapLogLevel.Info, $"parse: could not interpret the response from {finalUri}", ex);
            return new RdapResult(finalUri, body, null, ex);
        }
    }

    /// <summary>
    /// Finds the referral to a more specific server (e.g. registry → registrar for thin gTLDs):
    /// a <c>related</c> RDAP link to the same kind of object on a different URL.
    /// </summary>
    public static Uri? FindReferral(RdapResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Data is not Domain domain || domain.Links is null)
        {
            return null;
        }

        foreach (var link in domain.Links)
        {
            if (!string.Equals(link.Rel, "related", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(link.Href, UriKind.Absolute, out var href) ||
                href.Scheme is not ("https" or "http"))
            {
                continue;
            }

            var isRdap = link.Type is null
                ? href.AbsolutePath.Contains("/domain/", StringComparison.OrdinalIgnoreCase)
                : link.Type.Contains("rdap+json", StringComparison.OrdinalIgnoreCase);

            if (isRdap && Uri.Compare(href, result.RequestUri, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return href;
            }
        }

        return null;
    }

    private static RdapHttpException CreateHttpError(HttpResponseMessage response, string body, Uri uri)
    {
        var error = RdapResponseParser.TryParseError(body);
        var detail = string.Join(" ", new[] { error?.Title }.Concat(error?.Description ?? []).Where(s => !string.IsNullOrWhiteSpace(s)));

        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero, // a past or clock-skewed date means "now"
            _ => null,
        };

        var message = response.StatusCode switch
        {
            HttpStatusCode.NotFound => "No match found.",
            HttpStatusCode.TooManyRequests => "Rate limited by the server" + (retryAfter is { } r ? $"; retry after {Math.Ceiling(r.TotalSeconds)}s." : "."),
            HttpStatusCode.BadRequest => "The server rejected the query as malformed.",
            HttpStatusCode.NotImplemented => "The server does not support this kind of query.",
            _ => $"Server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
        };

        if (detail.Length > 0)
        {
            message += $" ({detail})";
        }

        return new RdapHttpException(message, response.StatusCode, uri, retryAfter);
    }
}
