using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text.Json;
using Rdap.Bootstrap;
using Rdap.Diagnostics;
using Rdap.Net;

namespace Rdap.Cli;

/// <summary>Exit codes, documented in the README.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int NotFound = 1;
    public const int UsageError = 2;
    public const int ServerError = 3;
}

/// <summary>The <c>rdap</c> command. Output streams and HTTP are injectable so tests can drive it in-process.</summary>
internal sealed class CliApp
{
    private static readonly string[] TypeNames = ["auto", "domain", "nameserver", "ip", "autnum", "entity"];

    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public CliApp(TextWriter output, TextWriter error)
    {
        _out = output;
        _err = error;
    }

    public bool UseColor { get; init; }

    /// <summary>Overrides the HTTP handler (tests).</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Overrides the bootstrap cache directory (tests). Defaults to the platform cache dir.</summary>
    public string? CacheDirectory { get; init; }

    public static string Version =>
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var queryArgument = new Argument<string>("query")
        {
            Description = "Domain, IP address or CIDR, AS number (AS15169 or 15169), nameserver, or entity handle.",
        };
        var typeOption = new Option<string>("--type", "-t")
        {
            Description = "Force the query type instead of detecting it.",
            DefaultValueFactory = _ => "auto",
        };
        typeOption.AcceptOnlyFromAmong(TypeNames);
        var serverOption = new Option<Uri?>("--server", "-s")
        {
            Description = "Query this RDAP base URL instead of using IANA bootstrap (e.g. https://rdap.arin.net/registry/).",
            CustomParser = result =>
            {
                var text = result.Tokens.Single().Value;
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                {
                    return uri;
                }

                result.AddError($"'{text}' is not an absolute http(s) URL.");
                return null;
            },
        };
        var jsonOption = new Option<bool>("--json", "-j") { Description = "Print the raw RDAP JSON (one document per response) instead of text." };
        var noFollowOption = new Option<bool>("--no-follow") { Description = "Don't follow referrals from the registry to the registrar." };
        var verboseOption = new Option<int>("--verbose", "-v")
        {
            Description = "Repeat for more detail on stderr. -v: notices, links, and a log of each request and bootstrap " +
                          "lookup with timings. -vv: also DNS, connection attempts, proxy, TLS, and environment. " +
                          "-vvv: also HTTP headers and full exception details.",
            Arity = ArgumentArity.Zero,
            // -vvv is bundled into -v -v -v; the count of occurrences is the verbosity.
            CustomParser = result => result.Parent is OptionResult option ? option.IdentifierTokenCount : 0,
        };
        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Per-request timeout in seconds.",
            DefaultValueFactory = _ => 15,
        };
        timeoutOption.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() <= 0)
            {
                r.AddError("--timeout must be a positive number of seconds.");
            }
        });
        var noColorOption = new Option<bool>("--no-color") { Description = "Disable coloured output." };
        var refreshOption = new Option<bool>("--refresh-bootstrap") { Description = "Re-download the IANA bootstrap files instead of using the cache." };
        var noDownloadOption = new Option<bool>("--no-bootstrap-download")
        {
            Description = "Never download the IANA bootstrap files; use the cached copy (even if stale) or the built-in snapshot.",
        };

        var root = new RootCommand("Look up domain, IP, ASN and entity registration data using RDAP — a modern whois.")
        {
            queryArgument, typeOption, serverOption, jsonOption, noFollowOption, verboseOption, timeoutOption, noColorOption, refreshOption, noDownloadOption,
        };

        root.Validators.Add(r =>
        {
            if (r.GetValue(refreshOption) && r.GetValue(noDownloadOption))
            {
                r.AddError("--refresh-bootstrap and --no-bootstrap-download can't be used together.");
            }
        });

        root.SetAction((parse, ct) => LookupAsync(
            new LookupOptions(
                Query: parse.GetValue(queryArgument)!,
                Type: parse.GetValue(typeOption) ?? "auto",
                Server: parse.GetValue(serverOption),
                Json: parse.GetValue(jsonOption),
                Follow: !parse.GetValue(noFollowOption),
                Verbosity: parse.GetValue(verboseOption),
                Timeout: TimeSpan.FromSeconds(parse.GetValue(timeoutOption)),
                Color: UseColor && !parse.GetValue(noColorOption),
                RefreshBootstrap: parse.GetValue(refreshOption),
                DownloadBootstrap: !parse.GetValue(noDownloadOption)),
            ct));

        var result = root.Parse(args);
        if (result.Action is ParseErrorAction)
        {
            // Report parse errors ourselves so they get the documented usage exit code.
            foreach (var error in result.Errors)
            {
                await _err.WriteLineAsync($"rdap: {error.Message}");
            }

            await _err.WriteLineAsync("Try 'rdap --help' for more information.");
            return ExitCodes.UsageError;
        }

        return await result.InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, cancellationToken);
    }

    private sealed record LookupOptions(
        string Query, string Type, Uri? Server, bool Json, bool Follow, int Verbosity, TimeSpan Timeout, bool Color, bool RefreshBootstrap, bool DownloadBootstrap)
    {
        public bool Verbose => Verbosity > 0;
    }

    private async Task<int> LookupAsync(LookupOptions options, CancellationToken cancellationToken)
    {
        RdapQuery query;
        try
        {
            QueryType? forced = options.Type == "auto" ? null : Enum.Parse<QueryType>(options.Type, ignoreCase: true);
            query = RdapQuery.Parse(options.Query, forced);
        }
        catch (ArgumentException ex)
        {
            await _err.WriteLineAsync($"rdap: {StripParamName(ex)}");
            return ExitCodes.UsageError;
        }

        var log = new StderrLogger(_err, options.Verbosity, options.Color);
        var cacheDirectory = CacheDirectory ?? BootstrapService.DefaultCacheDirectory();
        EnvironmentReport.Log(log, cacheDirectory, options.Timeout);
        log.Log(RdapLogLevel.Info, $"query: {query.Type} '{query.Value}' -> {query.Path}");

        using var http = CreateHttpClient(options.Timeout, log);
        var bootstrap = new BootstrapService(http, cacheDirectory)
        {
            ForceRefresh = options.RefreshBootstrap,
            AllowNetwork = options.DownloadBootstrap,
            Logger = log,
        };
        bootstrap.Warning += Warn;
        var client = new RdapClient(http, bootstrap) { Logger = log };

        RdapResult result;
        try
        {
            result = await client.QueryAsync(query, options.Server, cancellationToken);
        }
        catch (RdapHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            await _err.WriteLineAsync($"rdap: {ex.Message} ({ex.RequestUri})");
            return ExitCodes.NotFound;
        }
        catch (RdapNoServerException ex)
        {
            await _err.WriteLineAsync($"rdap: {ex.Message}");
            return ExitCodes.NotFound;
        }
        catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
        {
            log.Log(RdapLogLevel.Info, "lookup failed", ex);
            await _err.WriteLineAsync($"rdap: {DescribeNetworkError(ex, options.Timeout)}{VerboseHint(options)}");
            return ExitCodes.ServerError;
        }
        catch (RdapException ex)
        {
            log.Log(RdapLogLevel.Info, "lookup failed", ex);
            await _err.WriteLineAsync($"rdap: {ex.Message}");
            return ExitCodes.ServerError;
        }

        await WriteResultAsync(result, options, isReferral: false);

        if (options.Follow && RdapClient.FindReferral(result) is { } referral)
        {
            log.Log(RdapLogLevel.Info, $"referral: following {referral}");
            try
            {
                var referred = await client.GetAsync(referral, cancellationToken);
                await WriteResultAsync(referred, options, isReferral: true);
            }
            catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
            {
                // The registry answer already printed; a failing registrar shouldn't fail the lookup.
                log.Log(RdapLogLevel.Info, "referral failed", ex);
                Warn($"Could not follow referral to {referral}: {DescribeNetworkError(ex, options.Timeout)}");
            }
        }

        return ExitCodes.Success;
    }

    private async Task WriteResultAsync(RdapResult result, LookupOptions options, bool isReferral)
    {
        if (options.Json)
        {
            await _out.WriteLineAsync(Reindent(result.Json));
            return;
        }

        var renderer = new TextRenderer(_out, options.Color, options.Verbose);
        if (isReferral)
        {
            await _out.WriteLineAsync();
        }

        renderer.Header($"# {(isReferral ? "Referral" : "Source")}: {result.RequestUri}");
        await _out.WriteLineAsync();

        if (result.Data is { } data)
        {
            renderer.Render(data);
        }
        else
        {
            Warn($"Could not interpret the response ({result.ParseError?.Message}); showing raw JSON.");
            await _out.WriteLineAsync(Reindent(result.Json));
        }
    }

    private HttpClient CreateHttpClient(TimeSpan timeout, StderrLogger log)
    {
        HttpMessageHandler handler = Handler ?? CreateSocketsHandler(log);
        if (log.IsEnabled(RdapLogLevel.Info))
        {
            handler = new LoggingHandler(handler, log, Handler is null ? HttpClient.DefaultProxy : null);
        }

        // Disposing a LoggingHandler disposes the handler it wraps, so only dispose when we created it.
        var http = new HttpClient(handler, disposeHandler: Handler is null) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"rdap-dotnet-cli/{Version} (+https://github.com/chrismeller/rdap-dotnet-cli)");
        return http;
    }

    private static SocketsHttpHandler CreateSocketsHandler(StderrLogger log)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            // Race IPv6 and IPv4 instead of waiting out the whole timeout on an unreachable IPv6 address.
            ConnectCallback = new HappyEyeballsConnector(log).ConnectAsync,
        };

        if (log.IsEnabled(RdapLogLevel.Debug))
        {
            // Observe the certificate without changing the verdict: only a clean chain is accepted, as by default.
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
            {
                var host = (sender as SslStream)?.TargetHostName;
                log.Log(errors == SslPolicyErrors.None ? RdapLogLevel.Debug : RdapLogLevel.Info,
                    $"tls: {host} presented '{certificate?.Subject}' issued by '{certificate?.Issuer}', " +
                    $"valid {certificate?.GetEffectiveDateString()} to {certificate?.GetExpirationDateString()}: " +
                    (errors == SslPolicyErrors.None ? "trusted" : $"REJECTED ({errors})"));
                if (errors != SslPolicyErrors.None && chain is not null)
                {
                    foreach (var status in chain.ChainStatus)
                    {
                        log.Log(RdapLogLevel.Info, $"tls: chain status {status.Status}: {status.StatusInformation.Trim()}");
                    }
                }

                return errors == SslPolicyErrors.None;
            };
        }

        return handler;
    }

    private static string VerboseHint(LookupOptions options) =>
        options.Verbosity >= 2 ? "" : " Run again with -vv for connection details.";

    private void Warn(string message) =>
        _err.WriteLine(UseColor ? $"{Ansi.Yellow}warning:{Ansi.Reset} {message}" : $"warning: {message}");

    private static bool IsNetworkError(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or RdapHttpException ||
        (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static string DescribeNetworkError(Exception ex, TimeSpan timeout) => ex switch
    {
        TaskCanceledException => $"Request timed out after {timeout.TotalSeconds:0}s.",
        RdapHttpException h => $"{h.Message} ({h.RequestUri})",
        HttpRequestException { InnerException: { } inner } h => $"{h.Message} {inner.Message}",
        _ => ex.Message,
    };

    private static string StripParamName(ArgumentException ex) =>
        ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", "", StringComparison.Ordinal);

    /// <summary>Pretty-prints JSON without reflection (AOT-safe); returns the input unchanged if it isn't JSON.</summary>
    internal static string Reindent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                doc.WriteTo(writer);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
