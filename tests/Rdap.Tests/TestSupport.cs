using System.Net;
using System.Text;

namespace Rdap.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Serves canned responses by exact URL; anything else is a 404 (or throws, if configured).</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<Uri> Requests { get; } = [];

    public bool FailUnknown { get; set; }

    public FakeHandler Json(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/rdap+json"),
        };
        return this;
    }

    public FakeHandler Fixture(string url, string fixtureName, HttpStatusCode status = HttpStatusCode.OK) =>
        Json(url, Tests.Fixture.Read(fixtureName), status);

    public FakeHandler Respond(string url, Func<HttpResponseMessage> factory)
    {
        _routes[url] = factory;
        return this;
    }

    /// <summary>Serves the test bootstrap files at the real IANA URLs.</summary>
    public FakeHandler WithBootstrap()
    {
        foreach (var name in new[] { "dns", "ipv4", "ipv6", "asn", "object-tags" })
        {
            Fixture($"https://data.iana.org/rdap/{name}.json", $"bootstrap-{name}.json");
        }

        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        if (_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var factory))
        {
            var response = factory();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        if (FailUnknown)
        {
            throw new HttpRequestException($"No route for {request.RequestUri}");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request,
            Content = new StringContent(Tests.Fixture.Read("error-404.json"), Encoding.UTF8, "application/rdap+json"),
        });
    }
}

/// <summary>A unique temp directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rdap-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
