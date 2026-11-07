using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Rdap.Cli;
using Rdap.Diagnostics;

namespace Rdap.Tests;

public sealed class StderrLoggerTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, RdapLogLevel.Info)]
    [InlineData(2, RdapLogLevel.Debug)]
    [InlineData(3, RdapLogLevel.Trace)]
    [InlineData(9, RdapLogLevel.Trace)]
    public void Verbosity_maps_to_the_highest_enabled_level(int verbosity, RdapLogLevel? highest)
    {
        var log = new StderrLogger(TextWriter.Null, verbosity, color: false);

        foreach (var level in Enum.GetValues<RdapLogLevel>())
        {
            Assert.Equal(highest is { } h && level <= h, log.IsEnabled(level));
        }
    }

    [Fact]
    public void Describe_adds_the_platform_error_codes_messages_leave_out()
    {
        var socket = new SocketException((int)SocketError.ConnectionRefused);
        var http = new HttpRequestException(HttpRequestError.NameResolutionError, "Name or service not known");
        var tls = new AuthenticationException("The remote certificate is invalid.");

        Assert.Contains($"[SocketError=ConnectionRefused, errno={socket.NativeErrorCode}]", StderrLogger.Describe(socket, false), StringComparison.Ordinal);
        Assert.Contains("[HttpRequestError=NameResolutionError]", StderrLogger.Describe(http, false), StringComparison.Ordinal);
        Assert.Contains("[TLS handshake failed]", StderrLogger.Describe(tls, false), StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestError=", StderrLogger.Describe(new HttpRequestException("plain"), false), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_walks_inner_and_aggregate_exceptions()
    {
        var ex = new HttpRequestException("outer", new AggregateException(
            new SocketException((int)SocketError.NetworkUnreachable),
            new TimeoutException("too slow")));

        var lines = StderrLogger.Describe(ex, includeStackTrace: false).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("System.Net.Http.HttpRequestException: outer", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  ---> System.AggregateException:", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("    ---> System.Net.Sockets.SocketException:", lines[2], StringComparison.Ordinal);
        Assert.Equal("    ---> System.TimeoutException: too slow", lines[3]);
    }

    [Fact]
    public void Describe_includes_stack_traces_only_when_asked()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        Assert.Contains(nameof(Describe_includes_stack_traces_only_when_asked), StderrLogger.Describe(thrown, includeStackTrace: true), StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(Describe_includes_stack_traces_only_when_asked), StderrLogger.Describe(thrown, includeStackTrace: false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_exception_is_described_once_across_log_lines()
    {
        using var err = new StringWriter();
        var log = new StderrLogger(err, verbosity: 1, color: false);
        var ex = new HttpRequestException("connection failed");

        log.Log(RdapLogLevel.Info, "http#1: failed", ex);
        log.Log(RdapLogLevel.Info, "lookup failed", ex);

        var text = err.ToString();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "connection failed"));
        Assert.Contains("System.Net.Http.HttpRequestException (details above)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Colour_dims_the_timestamp()
    {
        using var err = new StringWriter();

        new StderrLogger(err, verbosity: 1, color: true).Log(RdapLogLevel.Info, "hello");

        Assert.Matches(@"^\e\[2m\[\s*\d+\.\d{3}\] \e\[0mhello", err.ToString());
    }

    [Fact]
    public void Messages_above_the_verbosity_are_dropped()
    {
        using var err = new StringWriter();

        new StderrLogger(err, verbosity: 1, color: false).Log(RdapLogLevel.Debug, "hidden");

        Assert.Equal("", err.ToString());
    }
}

public sealed class LoggingHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> Send(HttpMessageHandler inner, IWebProxy? proxy, string url = "https://rdap.example.test/domain/example.com")
    {
        var log = new ListLogger();
        using var http = new HttpClient(new LoggingHandler(inner, log, proxy));
        try
        {
            using var _ = await http.GetAsync(new Uri(url), Ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
        }

        return log.Messages;
    }

    [Fact]
    public async Task Reports_redirects()
    {
        var inner = new FakeHandler().Respond("https://rdap.example.test/domain/example.com", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        });
        // FakeHandler sets RequestMessage to the original request; emulate a redirect by rewriting it.
        var redirecting = new RewritingHandler(inner, new Uri("https://rdap.other.test/domain/example.com"));

        var messages = await Send(redirecting, proxy: null);

        Assert.Contains("http#1: redirected to https://rdap.other.test/domain/example.com", messages);
    }

    [Fact]
    public async Task Reports_cancellation_with_the_host()
    {
        var messages = await Send(new ThrowingHandler(new TaskCanceledException("timed out")), proxy: null);

        Assert.Contains(messages, m => m.StartsWith("http#1: cancelled after", StringComparison.Ordinal) && m.EndsWith("while waiting for rdap.example.test", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "proxy: none")]
    [InlineData("bypass", "proxy: none for this host (direct connection)")]
    [InlineData("direct", "proxy: none (direct connection)")]
    [InlineData("via", "proxy: via http://proxy.example.test:3128/")]
    [InlineData("throws", "proxy: could not evaluate (bad proxy config)")]
    public async Task Describes_the_proxy_for_each_request(string? mode, string expected)
    {
        IWebProxy? proxy = mode is null ? null : new StubProxy(mode);

        var messages = await Send(new FakeHandler().Json("https://rdap.example.test/domain/example.com", "{}"), proxy);

        Assert.Contains($"http#1: {expected}", messages);
    }

    [Fact]
    public async Task Sensitive_headers_are_redacted_at_trace_level()
    {
        var inner = new FakeHandler().Respond("https://rdap.example.test/domain/example.com", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            response.Headers.Add("Set-Cookie", "session=abc123");
            return response;
        });

        var messages = await Send(inner, proxy: null);

        Assert.Contains("http#1: < Set-Cookie: <redacted>", messages);
        Assert.DoesNotContain(messages, m => m.Contains("abc123", StringComparison.Ordinal));
    }

    private sealed class RewritingHandler(HttpMessageHandler inner, Uri finalUri) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
            return response;
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class StubProxy(string mode) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => mode switch
        {
            "direct" => destination,
            "throws" => throw new InvalidOperationException("bad proxy config"),
            _ => new Uri("http://proxy.example.test:3128/"),
        };

        public bool IsBypassed(Uri host) => mode == "bypass";
    }
}
