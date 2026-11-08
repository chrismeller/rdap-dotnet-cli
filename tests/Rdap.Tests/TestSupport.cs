using System.Net;
using System.Text;
using Rdap.Diagnostics;

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

/// <summary>Collects log messages at every level.</summary>
internal sealed class ListLogger : IRdapLogger
{
    public List<string> Messages { get; } = [];

    public bool IsEnabled(RdapLogLevel level) => true;

    public void Log(RdapLogLevel level, string message, Exception? exception = null)
    {
        lock (Messages)
        {
            Messages.Add(message);
        }
    }
}

/// <summary>
/// A minimal HTTP/1.1 server on 127.0.0.1 that answers one request with a fixed RDAP body,
/// so the real <see cref="SocketsHttpHandler"/> stack can be tested without the internet.
/// </summary>
internal sealed class LocalRdapServer : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly TaskCompletionSource<string> _path = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LocalRdapServer(System.Net.Sockets.TcpListener listener) => _listener = listener;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>The path of the request the server answered.</summary>
    public Task<string> RequestPath => _path.Task;

    public static Task<LocalRdapServer> StartAsync(string body, CancellationToken cancellationToken)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new LocalRdapServer(listener);
        _ = server.ServeAsync(body, cancellationToken);
        return Task.FromResult(server);
    }

    private async Task ServeAsync(string body, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/rdap+json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            _path.TrySetResult(requestLine.Split(' ') is [_, var path, ..] ? path : requestLine);
        }
        catch (Exception ex)
        {
            _path.TrySetException(ex);
        }
    }

    public void Dispose() => _listener.Stop();
}
