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

    [Theory]
    [InlineData("2023-12-28T17:24:56-05:00", "2023-12-28 22:24:56 UTC")]
    [InlineData("1997-09-15T07:00:00.000+00:00", "1997-09-15 07:00:00 UTC")]
    [InlineData("garbage", "garbage")]
    public void Formats_dates_in_utc(string input, string expected) =>
        Assert.Equal(expected, TextRenderer.FormatDate(input));
}
