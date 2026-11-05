using System.Globalization;
using System.Net;
using Rdap.Bootstrap;
using Rdap.Cli;
using Rdap.Models;

namespace Rdap.IntegrationTests;

/// <summary>Shared HTTP client, bootstrap cache and retry policy for live tests.</summary>
public sealed class LiveFixture : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "rdap-live-" + Guid.NewGuid().ToString("N"));

    public LiveFixture()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("rdap-dotnet-cli-integration-tests (+https://github.com/chrismeller/rdap-dotnet-cli)");
        Bootstrap = new BootstrapService(Http, _cacheDirectory);
        Client = new RdapClient(Http, Bootstrap);
    }

    public HttpClient Http { get; }
    public BootstrapService Bootstrap { get; }
    public RdapClient Client { get; }
    public string CacheDirectory => _cacheDirectory;

    public void Dispose()
    {
        Http.Dispose();
        try
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Retries transient failures (network errors, timeouts, 429, 5xx) so CI isn't flaky.
    /// Parse failures are never retried: they're what these tests exist to catch.
    /// </summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> action)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < 4 && IsTransient(ex))
            {
                var wait = ex is RdapHttpException { RetryAfter: { } retryAfter } && retryAfter < TimeSpan.FromSeconds(30) ? retryAfter : delay;
                await Task.Delay(wait, TestContext.Current.CancellationToken);
                delay *= 2;
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        RdapHttpException h => h.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError,
        HttpRequestException or TaskCanceledException => !TestContext.Current.CancellationToken.IsCancellationRequested,
        RdapException { InnerException: null } r => r.Message.StartsWith("Could not load the IANA", StringComparison.Ordinal),
        _ => false,
    };
}

[CollectionDefinition(Name)]
public sealed class LiveTestGroup : ICollectionFixture<LiveFixture>
{
    public const string Name = "live";
}

/// <summary>Structural checks applied to every live response.</summary>
internal static class Invariants
{
    public static T AssertParsed<T>(RdapResult result)
        where T : RdapObject
    {
        Assert.True(result.ParseError is null,
            $"Response from {result.RequestUri} failed to parse: {result.ParseError?.Message}\n{Truncate(result.Json)}");
        var data = Assert.IsType<T>(result.Data);
        AssertObject(data, result.RequestUri, "$");
        AssertRenders(data);
        return data;
    }

    private static void AssertObject(RdapObject obj, Uri source, string path)
    {
        foreach (var (ev, i) in (obj.Events ?? []).Select((e, i) => (e, i)))
        {
            Assert.False(string.IsNullOrWhiteSpace(ev.EventAction), $"{source} {path}.events[{i}] has no eventAction");
            Assert.True(
                DateTimeOffset.TryParse(ev.EventDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
                $"{source} {path}.events[{i}] eventDate '{ev.EventDate}' is not a parseable date");
        }

        foreach (var (entity, i) in (obj.Entities ?? []).Select((e, i) => (e, i)))
        {
            var entityPath = $"{path}.entities[{i}]";
            Assert.Equal("entity", entity.ObjectClassName, ignoreCase: true);
            if (entity.VCardArray is not null)
            {
                Assert.True(entity.VCard is not null, $"{source} {entityPath}.vcardArray is present but couldn't be parsed as a jCard");
            }

            AssertObject(entity, source, entityPath);
        }

        foreach (var (link, i) in (obj.Links ?? []).Select((l, i) => (l, i)))
        {
            Assert.False(string.IsNullOrWhiteSpace(link.Href), $"{source} {path}.links[{i}] has no href");
        }
    }

    private static void AssertRenders(RdapObject obj)
    {
        using var writer = new StringWriter();
        new TextRenderer(writer, color: false, verbose: true).Render(obj);
        Assert.False(string.IsNullOrWhiteSpace(writer.ToString()));
    }

    private static string Truncate(string json) => json.Length <= 2000 ? json : json[..2000] + "…";
}
