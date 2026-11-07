using System.Net;
using Rdap.Bootstrap;
using Rdap.Cli;
using Rdap.Models;

namespace Rdap.IntegrationTests;

[Collection(LiveTestGroup.Name)]
public class LiveBootstrapTests(LiveFixture live)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(BootstrapKind.Dns)]
    [InlineData(BootstrapKind.Ipv4)]
    [InlineData(BootstrapKind.Ipv6)]
    [InlineData(BootstrapKind.Asn)]
    [InlineData(BootstrapKind.ObjectTags)]
    public async Task Iana_bootstrap_files_parse(BootstrapKind kind)
    {
        var file = await LiveFixture.RetryAsync(() => live.Bootstrap.GetFileAsync(kind, Ct));

        Assert.NotEmpty(file.Services);
        Assert.All(file.Services, s => Assert.InRange(s.Count, 2, 3));
    }

    [Fact]
    public async Task Iana_bootstrap_resolves_well_known_registries()
    {
        var registry = await LiveFixture.RetryAsync(() => live.Bootstrap.GetRegistryAsync(Enum.GetValues<BootstrapKind>(), Ct));

        Assert.Contains("verisign", registry.FindDomainServer("google.com")!.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arin", registry.FindIpServer(IPAddress.Parse("8.8.8.8"))!.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ripe", registry.FindIpServer(IPAddress.Parse("193.0.6.139"))!.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apnic", registry.FindAsnServer(4608)!.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arin", registry.FindEntityServer("ABUSE5250-ARIN")!.Host, StringComparison.OrdinalIgnoreCase);
    }
}

[Collection(LiveTestGroup.Name)]
public class LiveLookupTests(LiveFixture live)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<RdapResult> Query(string input, QueryType? type = null, string? server = null) =>
        LiveFixture.RetryAsync(() => live.Client.QueryAsync(RdapQuery.Parse(input, type), server is null ? null : new Uri(server), Ct));

    // A spread of registry operators, so a format change at any one of them shows up.
    [Theory]
    [InlineData("google.com")]       // Verisign (thin registry → registrar referral)
    [InlineData("wikipedia.org")]    // Public Interest Registry
    [InlineData("google.dev")]       // Google Registry
    [InlineData("registro.br")]      // ccTLD (NIC.br)
    public async Task Domain_lookup_parses(string name)
    {
        var domain = Invariants.AssertParsed<Domain>(await Query(name));

        Assert.Equal(name, domain.LdhName, ignoreCase: true);
        Assert.NotEmpty(domain.Nameservers ?? []);
        Assert.Contains(domain.Events ?? [], e => e.EventAction == "registration");
    }

    [Fact]
    public async Task Registrar_referral_parses()
    {
        var registry = await Query("google.com");
        var referral = RdapClient.FindReferral(registry);
        Assert.NotNull(referral);

        var registrar = await LiveFixture.RetryAsync(() => live.Client.GetAsync(referral, Ct));
        var domain = Invariants.AssertParsed<Domain>(registrar);

        Assert.Equal("google.com", domain.LdhName, ignoreCase: true);
        Assert.Contains(domain.Entities ?? [], e => e.Roles?.Contains("registrar") == true);
    }

    [Fact]
    public async Task Registry_lists_registrar_with_iana_id()
    {
        var domain = Invariants.AssertParsed<Domain>(await Query("google.com"));

        var registrar = Assert.Single(domain.Entities!, e => e.Roles?.Contains("registrar") == true);
        Assert.Contains(registrar.PublicIds ?? [], id => id.Type == "IANA Registrar ID" && id.Identifier == "292");
        Assert.Equal("MarkMonitor Inc.", registrar.VCard?.FormattedName);
    }

    // One address per RIR.
    [Theory]
    [InlineData("8.8.8.8")]                // ARIN
    [InlineData("2001:4860:4860::8888")]   // ARIN, IPv6
    [InlineData("193.0.6.139")]            // RIPE NCC
    [InlineData("1.1.1.1")]                // APNIC
    [InlineData("200.160.2.3")]            // LACNIC (NIC.br)
    [InlineData("196.216.2.1")]            // AFRINIC
    public async Task Ip_lookup_parses(string ip)
    {
        var network = Invariants.AssertParsed<IpNetwork>(await Query(ip));

        Assert.False(string.IsNullOrWhiteSpace(network.Handle));
        var address = IPAddress.Parse(ip);
        var start = IPAddress.Parse(network.StartAddress!);
        var end = IPAddress.Parse(network.EndAddress!);
        Assert.Equal(address.AddressFamily, start.AddressFamily);
        Assert.True(Compare(start, address) <= 0 && Compare(address, end) <= 0,
            $"{ip} is not within the returned range {start} - {end}");
    }

    [Fact]
    public async Task Cidr_lookup_parses()
    {
        var network = Invariants.AssertParsed<IpNetwork>(await Query("8.8.8.0/24"));

        Assert.Equal("8.8.8.0", network.StartAddress);
    }

    [Theory]
    [InlineData("AS15169", 15169)]  // ARIN
    [InlineData("AS3333", 3333)]    // RIPE NCC
    [InlineData("AS4608", 4608)]    // APNIC
    public async Task Autnum_lookup_parses(string input, long asn)
    {
        var autnum = Invariants.AssertParsed<Autnum>(await Query(input));

        Assert.InRange(asn, autnum.StartAutnum!.Value, (autnum.EndAutnum ?? autnum.StartAutnum).Value);
    }

    [Fact]
    public async Task Nameserver_lookup_parses()
    {
        var ns = Invariants.AssertParsed<Nameserver>(await Query("ns1.google.com", QueryType.Nameserver));

        Assert.Equal("ns1.google.com", ns.LdhName, ignoreCase: true);
    }

    [Fact]
    public async Task Entity_lookup_by_object_tag_parses()
    {
        var entity = Invariants.AssertParsed<Entity>(await Query("ABUSE5250-ARIN"));

        Assert.Equal("ABUSE5250-ARIN", entity.Handle, ignoreCase: true);
        Assert.NotNull(entity.VCard);
    }

    [Fact]
    public async Task Entity_lookup_with_explicit_server_parses()
    {
        var entity = Invariants.AssertParsed<Entity>(await Query("GOGL", server: "https://rdap.arin.net/registry/"));

        Assert.Equal("Google LLC", entity.VCard?.FormattedName);
    }

    [Fact]
    public async Task Unregistered_domain_is_not_found()
    {
        var name = $"rdap-dotnet-cli-{Guid.NewGuid():N}.com";

        var ex = await Assert.ThrowsAsync<RdapHttpException>(() => Query(name));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    private static int Compare(IPAddress a, IPAddress b)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        for (var i = 0; i < x.Length; i++)
        {
            var c = x[i].CompareTo(y[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }
}

/// <summary>Runs the actual command end to end against live servers.</summary>
[Collection(LiveTestGroup.Name)]
public class LiveCliTests(LiveFixture live)
{
    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        // Retry the whole command on transient (exit 3) failures.
        for (var attempt = 1; ; attempt++)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await new CliApp(output, error) { CacheDirectory = live.CacheDirectory }.RunAsync(args, TestContext.Current.CancellationToken);
            if (exit != 3 || attempt == 3)
            {
                return (exit, output.ToString(), error.ToString());
            }

            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Domain_with_referral()
    {
        var (exit, output, error) = await Run("google.com");

        Assert.True(exit == 0, error);
        Assert.Matches(@"Domain Name:\s+GOOGLE\.COM", output);
        Assert.Matches(@"(?i)Name Server:\s+ns1\.google\.com", output);
        Assert.Contains("# Referral:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not interpret", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ip_text_output()
    {
        var (exit, output, error) = await Run("8.8.8.8");

        Assert.True(exit == 0, error);
        Assert.Matches(@"Range:\s+8\.8\.8\.0 - 8\.8\.8\.255", output);
        Assert.Equal("", error);
    }

    [Fact]
    public async Task Json_output()
    {
        var (exit, output, error) = await Run("AS15169", "--json");

        Assert.True(exit == 0, error);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal("autnum", doc.RootElement.GetProperty("objectClassName").GetString());
    }

    [Fact]
    public async Task Verbose_diagnostics_trace_the_real_connection()
    {
        // The production handler: Happy Eyeballs connect plus the -vv TLS observer, against a real registry.
        var (exit, _, error) = await Run("google.com", "--no-follow", "--no-color", "-vv");

        Assert.True(exit == 0, error);
        Assert.Matches(@"dns: rdap\.verisign\.com -> ", error);
        Assert.Matches(@"connect: connected to \S+:443 in \d+ ms", error);
        Assert.Matches(@"tls: rdap\.verisign\.com presented '.+' issued by '.+', valid .+: trusted", error);
        Assert.Matches(@"http#\d+: 200 OK", error);
    }

    [Fact]
    public async Task No_bootstrap_download_still_resolves()
    {
        // Warm the shared cache first, so this doesn't depend on the build having embedded a snapshot.
        Assert.Equal(0, (await Run("8.8.8.8", "--no-follow")).Exit);

        var (exit, output, error) = await Run("8.8.8.8", "--no-bootstrap-download", "--no-color", "-v");

        Assert.True(exit == 0, error);
        Assert.Matches(@"Range:\s+8\.8\.8\.0 - 8\.8\.8\.255", output);
        Assert.Contains("downloads disabled, not contacting IANA", error, StringComparison.Ordinal);
        Assert.DoesNotContain("bootstrap: ipv4.json: downloading", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_found_exit_code()
    {
        var (exit, _, _) = await Run($"rdap-dotnet-cli-{Guid.NewGuid():N}.com");

        Assert.Equal(1, exit);
    }
}
