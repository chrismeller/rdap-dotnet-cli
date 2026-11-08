using System.Text.Json;
using Rdap.Cli;

namespace Rdap.Tests;

public sealed class CliAppTests : IDisposable
{
    private readonly TempDirectory _cache = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _cache.Dispose();
        _out.Dispose();
        _err.Dispose();
    }

    private static FakeHandler GoogleHandler() => new FakeHandler().WithBootstrap()
        .Fixture("https://rdap.verisign.com/com/v1/domain/google.com", "domain-verisign-google.json")
        .Fixture("https://rdap.markmonitor.com/rdap/domain/GOOGLE.COM", "domain-markmonitor-google.json");

    private Task<int> Run(FakeHandler handler, params string[] args) =>
        new CliApp(_out, _err) { Handler = handler, CacheDirectory = _cache.Path }.RunAsync(args, Ct);

    [Fact]
    public async Task Domain_lookup_follows_referral()
    {
        var exit = await Run(GoogleHandler(), "google.com");
        var text = _out.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("# Source: https://rdap.verisign.com/com/v1/domain/google.com", text, StringComparison.Ordinal);
        Assert.Contains("# Referral: https://rdap.markmonitor.com/rdap/domain/GOOGLE.COM", text, StringComparison.Ordinal);
        Assert.Matches(@"Domain Name:\s+GOOGLE\.COM", text);
        Assert.Matches(@"Name Server:\s+NS1\.GOOGLE\.COM", text);
        Assert.Matches(@"Registered:\s+1997-09-15 04:00:00 UTC", text);
        Assert.Matches(@"IANA Registrar ID:\s+292", text);
        Assert.Matches(@"Email:\s+abusecomplaints@markmonitor\.com", text);
        Assert.Matches(@"Redacted:\s+Registrant Name \(Server policy\)", text);
        Assert.Matches(@"Fax:\s+\+1\.6502530001", text);
        Assert.DoesNotContain("Terms of Use", text, StringComparison.Ordinal); // notices only with -v
        Assert.DoesNotContain("\e[", text, StringComparison.Ordinal);
        Assert.Equal("", _err.ToString());
    }

    [Fact]
    public async Task No_follow_stops_at_registry()
    {
        var handler = GoogleHandler();
        var exit = await Run(handler, "google.com", "--no-follow");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("# Referral", _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, u => u.Host == "rdap.markmonitor.com");
    }

    [Fact]
    public async Task Verbose_includes_notices_and_links()
    {
        await Run(GoogleHandler(), "google.com", "-v", "--no-follow");

        Assert.Contains("Notice: Terms of Use", _out.ToString(), StringComparison.Ordinal);
        Assert.Matches(@"Link:\s+https://rdap\.markmonitor\.com/rdap/domain/GOOGLE\.COM \(related\)", _out.ToString());
    }

    [Fact]
    public async Task Json_emits_one_document_per_response()
    {
        var exit = await Run(GoogleHandler(), "google.com", "--json");

        Assert.Equal(0, exit);
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(_out.ToString()), new JsonReaderOptions { AllowMultipleValues = true });
        var documents = new List<string?>();
        while (reader.Read())
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            documents.Add(doc.RootElement.GetProperty("ldhName").GetString());
        }

        Assert.Equal(["GOOGLE.COM", "google.com"], documents);
    }

    [Fact]
    public async Task Failed_referral_warns_but_succeeds()
    {
        var handler = new FakeHandler().WithBootstrap()
            .Fixture("https://rdap.verisign.com/com/v1/domain/google.com", "domain-verisign-google.json")
            .Respond("https://rdap.markmonitor.com/rdap/domain/GOOGLE.COM", () => throw new HttpRequestException("connection refused"));

        var exit = await Run(handler, "google.com");

        Assert.Equal(0, exit);
        Assert.Contains("warning: Could not follow referral", _err.ToString(), StringComparison.Ordinal);
        Assert.Matches(@"Domain Name:\s+GOOGLE\.COM", _out.ToString());
    }

    [Theory]
    [InlineData("8.8.8.8", "ip-arin-8.8.8.8.json", "https://rdap.arin.net/registry/ip/8.8.8.8", @"Range:\s+8\.8\.8\.0 - 8\.8\.8\.255")]
    [InlineData("AS3333", "autnum-ripe-3333.json", "https://rdap.db.ripe.net/autnum/3333", @"AS Number:\s+AS3333")]
    [InlineData("ABUSE5250-ARIN", "entity-arin-gogl.json", "https://rdap.arin.net/registry/entity/ABUSE5250-ARIN", @"Name:\s+Google LLC")]
    public async Task Renders_other_object_classes(string query, string fixture, string url, string expected)
    {
        var exit = await Run(new FakeHandler().WithBootstrap().Fixture(url, fixture), query);

        Assert.Equal(0, exit);
        Assert.Matches(expected, _out.ToString());
    }

    [Fact]
    public async Task Nameserver_lookup()
    {
        var handler = new FakeHandler().WithBootstrap()
            .Fixture("https://rdap.verisign.com/com/v1/nameserver/ns1.google.com", "nameserver-verisign.json");

        var exit = await Run(handler, "-t", "nameserver", "ns1.google.com");

        Assert.Equal(0, exit);
        Assert.Matches(@"IP Address:\s+216\.239\.32\.10", _out.ToString());
    }

    [Fact]
    public async Task Not_found_exits_1()
    {
        var exit = await Run(new FakeHandler().WithBootstrap(), "doesnotexist.com");

        Assert.Equal(1, exit);
        Assert.Contains("No match found", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_tld_exits_1()
    {
        var exit = await Run(new FakeHandler().WithBootstrap(), "example.zz");

        Assert.Equal(1, exit);
        Assert.Contains("No RDAP server", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("--type", "bogus", "x")]
    [InlineData("--server", "not a url", "x.com")]
    [InlineData("--timeout", "0", "x.com")]
    [InlineData("8.8.8.8/99", "-t", "ip")]
    public async Task Usage_errors_exit_2(params string[] args)
    {
        var exit = await Run(new FakeHandler { FailUnknown = true }, args);

        Assert.Equal(2, exit);
        Assert.StartsWith("rdap: ", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_error_exits_3()
    {
        var exit = await Run(new FakeHandler { FailUnknown = true }, "example.com", "--server", "https://rdap.example.test/");

        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task Server_error_exits_3()
    {
        var handler = new FakeHandler().Json("https://rdap.example.test/domain/example.com", "{}", System.Net.HttpStatusCode.InternalServerError);

        var exit = await Run(handler, "example.com", "--server", "https://rdap.example.test/");

        Assert.Equal(3, exit);
        Assert.Contains("HTTP 500", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unparseable_response_falls_back_to_raw_json()
    {
        var handler = new FakeHandler().Json("https://rdap.example.test/domain/example.com", """{"objectClassName":"domain","status":42}""");

        var exit = await Run(handler, "example.com", "--server", "https://rdap.example.test/");

        Assert.Equal(0, exit);
        Assert.Contains("Could not interpret the response", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\": 42", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_and_version_exit_0()
    {
        Assert.Equal(0, await Run(new FakeHandler(), "--help"));
        Assert.Contains("--no-follow", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await Run(new FakeHandler(), "--version"));
    }

    [Fact]
    public async Task Single_v_logs_requests_and_bootstrap_to_stderr_only()
    {
        var exit = await Run(GoogleHandler(), "google.com", "-v", "--no-follow");
        var err = _err.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("bootstrap: dns.json: downloading https://data.iana.org/rdap/dns.json", err, StringComparison.Ordinal);
        Assert.Contains("http#2: GET https://rdap.verisign.com/com/v1/domain/google.com", err, StringComparison.Ordinal);
        Assert.Matches(@"http#2: 200 OK in \d+ ms", err);
        Assert.DoesNotContain("env:", err, StringComparison.Ordinal); // -vv and up
        Assert.DoesNotContain("> User-Agent", err, StringComparison.Ordinal); // -vvv only
        Assert.DoesNotContain("http#", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-vvv")]
    [InlineData("-v", "-v", "-v")]
    [InlineData("--verbose", "-vv")]
    public async Task Triple_v_adds_environment_and_headers(params string[] flags)
    {
        var exit = await Run(GoogleHandler(), ["google.com", "--no-follow", .. flags]);
        var err = _err.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("env: rdap ", err, StringComparison.Ordinal);
        Assert.Contains("> User-Agent: rdap-dotnet-cli/", err, StringComparison.Ordinal);
        Assert.Contains("< Content-Type: application/rdap+json", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verbose_failure_reports_the_exception_chain()
    {
        var exit = await Run(new FakeHandler { FailUnknown = true }, "example.com", "--server", "https://rdap.example.test/", "-v");
        var err = _err.ToString();

        Assert.Equal(3, exit);
        Assert.Contains("System.Net.Http.HttpRequestException: No route for https://rdap.example.test/domain/example.com", err, StringComparison.Ordinal);
        Assert.Contains("Run again with -vv", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_bootstrap_download_never_contacts_iana()
    {
        // A stale cache stands in for the embedded snapshot, which builds without network access lack.
        var path = Path.Combine(_cache.Path, "dns.json");
        await File.WriteAllTextAsync(path, Fixture.Read("bootstrap-dns.json"), Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        var handler = GoogleHandler();

        var exit = await Run(handler, "google.com", "--no-follow", "--no-bootstrap-download");

        Assert.Equal(0, exit);
        Assert.DoesNotContain(handler.Requests, u => u.Host == "data.iana.org");
        Assert.Equal("", _err.ToString());
    }

    [Theory]
    [InlineData("http://user:secret@proxy:3128", "http://***@proxy:3128")]
    [InlineData("user:secret@proxy:3128", "***@proxy:3128")]
    [InlineData("proxy:3128", "proxy:3128")]
    [InlineData("a.test,u:p@b.test", "a.test,***@b.test")]
    public void Proxy_credentials_are_redacted(string value, string expected) =>
        Assert.Equal(expected, EnvironmentReport.RedactProxyValue(value));

    [Fact]
    public async Task No_color_also_applies_to_diagnostics()
    {
        await new CliApp(_out, _err) { Handler = GoogleHandler(), CacheDirectory = _cache.Path, UseColor = true }
            .RunAsync(["google.com", "--no-follow", "-v", "--no-color"], Ct);

        Assert.DoesNotContain("\e[", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_bootstrap_download_conflicts_with_refresh()
    {
        var exit = await Run(GoogleHandler(), "google.com", "--no-bootstrap-download", "--refresh-bootstrap");

        Assert.Equal(2, exit);
        Assert.Contains("can't be used together", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Colour_applies_to_output_warnings_and_diagnostics()
    {
        var handler = GoogleHandler();
        handler.Respond("https://rdap.markmonitor.com/rdap/domain/GOOGLE.COM", () => throw new HttpRequestException("registrar down"));

        var exit = await new CliApp(_out, _err) { Handler = handler, CacheDirectory = _cache.Path, UseColor = true }
            .RunAsync(["google.com", "-v"], Ct);

        Assert.Equal(0, exit);
        Assert.Contains("\e[1m\e[33m# Source:", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\e[36mDomain Name:", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\e[33mwarning:\e[0m Could not follow referral", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("\e[2m[", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_is_reported_with_the_configured_limit()
    {
        var handler = new FakeHandler().Respond("https://rdap.example.test/domain/example.com", () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var exit = await Run(handler, "example.com", "--server", "https://rdap.example.test/", "--timeout", "7");

        Assert.Equal(3, exit);
        Assert.Contains("rdap: Request timed out after 7s. Run again with -vv for connection details.", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_error_includes_the_inner_cause_and_drops_the_hint_at_vv()
    {
        var cause = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
        var handler = new FakeHandler().Respond("https://rdap.example.test/domain/example.com",
            () => throw new HttpRequestException("Connection failed.", cause));

        var exit = await Run(handler, "example.com", "--server", "https://rdap.example.test/", "-vv");

        Assert.Equal(3, exit);
        Assert.EndsWith($"rdap: Connection failed. {cause.Message}", _err.ToString().TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("Run again with -vv", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_mode_passes_non_json_bodies_through_unchanged()
    {
        var handler = new FakeHandler().Json("https://rdap.example.test/domain/example.com", "<html>not rdap</html>");

        var exit = await Run(handler, "example.com", "--server", "https://rdap.example.test/", "--json");

        Assert.Equal(0, exit);
        Assert.Equal("<html>not rdap</html>", _out.ToString().Trim());
    }

    [Fact]
    public async Task Invalid_server_url_is_a_usage_error()
    {
        var exit = await Run(new FakeHandler(), "example.com", "--server", "ftp://rdap.example.test/");

        Assert.Equal(2, exit);
        Assert.Contains("is not an absolute http(s) URL", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_positive_timeout_is_a_usage_error()
    {
        var exit = await Run(new FakeHandler(), "example.com", "--timeout", "0");

        Assert.Equal(2, exit);
        Assert.Contains("--timeout must be a positive number", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_http_stack_connects_through_happy_eyeballs()
    {
        // No injected handler: exercises the production SocketsHttpHandler + HappyEyeballsConnector against a local server.
        using var server = await LocalRdapServer.StartAsync(Fixture.Read("domain-verisign-google.json"), Ct);

        var exit = await new CliApp(_out, _err) { CacheDirectory = _cache.Path }
            .RunAsync(["google.com", "--server", server.BaseUrl, "--no-follow", "-vv"], Ct);
        var err = _err.ToString();

        Assert.True(exit == 0, err);
        Assert.Matches(@"Domain Name:\s+GOOGLE\.COM", _out.ToString());
        Assert.Contains($"connect: connected to 127.0.0.1:{server.Port}", err, StringComparison.Ordinal);
        Assert.Contains("http#1: proxy:", err, StringComparison.Ordinal);
        Assert.Contains("env: timeout 15s per request", err, StringComparison.Ordinal);
        Assert.Equal("/domain/google.com", await server.RequestPath);
    }

    [Theory]
    [InlineData("2023-12-28T17:24:56-05:00", "2023-12-28 22:24:56 UTC")]
    [InlineData("1997-09-15T07:00:00.000+00:00", "1997-09-15 07:00:00 UTC")]
    [InlineData("garbage", "garbage")]
    public void Formats_dates_in_utc(string input, string expected) =>
        Assert.Equal(expected, TextRenderer.FormatDate(input));
}
