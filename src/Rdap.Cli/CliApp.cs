using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Rdap.Bootstrap;

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
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Include notices (terms of service) and links." };
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

        var root = new RootCommand("Look up domain, IP, ASN and entity registration data using RDAP — a modern whois.")
        {
            queryArgument, typeOption, serverOption, jsonOption, noFollowOption, verboseOption, timeoutOption, noColorOption, refreshOption,
        };

        root.SetAction((parse, ct) => LookupAsync(
            new LookupOptions(
                Query: parse.GetValue(queryArgument)!,
                Type: parse.GetValue(typeOption) ?? "auto",
                Server: parse.GetValue(serverOption),
                Json: parse.GetValue(jsonOption),
                Follow: !parse.GetValue(noFollowOption),
                Verbose: parse.GetValue(verboseOption),
                Timeout: TimeSpan.FromSeconds(parse.GetValue(timeoutOption)),
                Color: UseColor && !parse.GetValue(noColorOption),
                RefreshBootstrap: parse.GetValue(refreshOption)),
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
        string Query, string Type, Uri? Server, bool Json, bool Follow, bool Verbose, TimeSpan Timeout, bool Color, bool RefreshBootstrap);

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

        using var http = CreateHttpClient(options.Timeout);
        var bootstrap = new BootstrapService(http, CacheDirectory ?? BootstrapService.DefaultCacheDirectory())
        {
            ForceRefresh = options.RefreshBootstrap,
        };
        bootstrap.Warning += Warn;
        var client = new RdapClient(http, bootstrap);

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
            await _err.WriteLineAsync($"rdap: {DescribeNetworkError(ex, options.Timeout)}");
            return ExitCodes.ServerError;
        }
        catch (RdapException ex)
        {
            await _err.WriteLineAsync($"rdap: {ex.Message}");
            return ExitCodes.ServerError;
        }

        await WriteResultAsync(result, options, isReferral: false);

        if (options.Follow && RdapClient.FindReferral(result) is { } referral)
        {
            try
            {
                var referred = await client.GetAsync(referral, cancellationToken);
                await WriteResultAsync(referred, options, isReferral: true);
            }
            catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
            {
                // The registry answer already printed; a failing registrar shouldn't fail the lookup.
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

    private HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var handler = Handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var http = new HttpClient(handler, disposeHandler: Handler is null) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"rdap-dotnet-cli/{Version} (+https://github.com/chrismeller/rdap-dotnet-cli)");
        return http;
    }

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
