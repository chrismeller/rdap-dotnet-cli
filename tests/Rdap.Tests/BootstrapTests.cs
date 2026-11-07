using System.Net;
using Rdap.Bootstrap;

namespace Rdap.Tests;

public class BootstrapRegistryTests
{
    private static readonly BootstrapRegistry Registry = new(
        BootstrapFile.Parse(Fixture.Read("bootstrap-dns.json")),
        BootstrapFile.Parse(Fixture.Read("bootstrap-ipv4.json")),
        BootstrapFile.Parse(Fixture.Read("bootstrap-ipv6.json")),
        BootstrapFile.Parse(Fixture.Read("bootstrap-asn.json")),
        BootstrapFile.Parse(Fixture.Read("bootstrap-object-tags.json")));

    [Theory]
    [InlineData("google.com", "https://rdap.verisign.com/com/v1/")]
    [InlineData("sub.domain.net", "https://rdap.verisign.com/com/v1/")]
    [InlineData("registro.br", "https://rdap.registro.br/")]           // prefers https over the listed-first http
    [InlineData("foo.co.example", "https://rdap.co-example.test/")]    // longest suffix wins
    [InlineData("foo.example", "https://rdap.example-tld.test/")]      // trailing slash added
    [InlineData("example", "https://rdap.example-tld.test/")]
    public void Finds_domain_server(string name, string expected) =>
        Assert.Equal(expected, Registry.FindDomainServer(name)?.AbsoluteUri);

    [Theory]
    [InlineData("google.xyz")]
    [InlineData("notcom")]
    public void Unknown_tld_has_no_server(string name) => Assert.Null(Registry.FindDomainServer(name));

    [Theory]
    [InlineData("8.8.8.8", "https://rdap.arin.net/registry/")]
    [InlineData("193.0.6.139", "https://rdap.db.ripe.net/")]
    [InlineData("1.1.1.1", "https://rdap.apnic.net/")]
    [InlineData("2001:4860:4860::8888", "https://rdap.arin.net/registry/")]
    [InlineData("2001:67c:2e8::1", "https://rdap.db.ripe.net/")]
    [InlineData("::ffff:8.8.8.8", "https://rdap.arin.net/registry/")]
    public void Finds_ip_server(string ip, string expected) =>
        Assert.Equal(expected, Registry.FindIpServer(IPAddress.Parse(ip))?.AbsoluteUri);

    [Fact]
    public void Unallocated_ip_has_no_server() => Assert.Null(Registry.FindIpServer(IPAddress.Parse("100.64.0.1")));

    [Theory]
    [InlineData(1, "https://rdap.arin.net/registry/")]
    [InlineData(1876, "https://rdap.arin.net/registry/")]
    [InlineData(15169, "https://rdap.arin.net/registry/")]
    [InlineData(3333, "https://rdap.db.ripe.net/")]
    [InlineData(4608, "https://rdap.apnic.net/")]
    public void Finds_asn_server(long asn, string expected) =>
        Assert.Equal(expected, Registry.FindAsnServer(asn)?.AbsoluteUri);

    [Fact]
    public void Unknown_asn_has_no_server() => Assert.Null(Registry.FindAsnServer(1877));

    [Theory]
    [InlineData("ABUSE5250-ARIN", "https://rdap.arin.net/registry/")]
    [InlineData("GII-ripe", "https://rdap.db.ripe.net/")]
    public void Finds_entity_server_by_object_tag(string handle, string expected) =>
        Assert.Equal(expected, Registry.FindEntityServer(handle)?.AbsoluteUri);

    [Theory]
    [InlineData("GOGL")]
    [InlineData("TRAILING-")]
    [InlineData("X-UNKNOWNTAG")]
    public void Entity_without_known_tag_has_no_server(string handle) => Assert.Null(Registry.FindEntityServer(handle));

    [Fact]
    public void Asn_entries_with_an_unparseable_range_end_are_skipped()
    {
        var registry = new BootstrapRegistry(null, null, null, BootstrapFile.Parse("""
            {"services": [
              [["100-oops"], ["https://rdap.bad.test/"]],
              [["100-200"], ["https://rdap.good.test/"]]
            ]}
            """), null);

        Assert.Equal(new Uri("https://rdap.good.test/"), registry.FindAsnServer(150));
    }

    [Fact]
    public void Services_without_an_http_url_are_ignored()
    {
        var registry = new BootstrapRegistry(BootstrapFile.Parse("""{"services": [[["test"], ["ftp://rdap.example.test/"]]]}"""), null, null, null, null);

        Assert.Null(registry.FindDomainServer("example.test"));
    }

    [Fact]
    public void Missing_files_find_nothing()
    {
        var empty = new BootstrapRegistry(null, null, null, null, null);

        Assert.Null(empty.FindDomainServer("example.com"));
        Assert.Null(empty.FindIpServer(IPAddress.Parse("8.8.8.8")));
        Assert.Null(empty.FindIpServer(IPAddress.Parse("2001:db8::1")));
        Assert.Null(empty.FindAsnServer(15169));
        Assert.Null(empty.FindEntityServer("X-ARIN"));
    }
}

public class BootstrapServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Downloads_and_caches()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler().WithBootstrap();
        using var http = new HttpClient(handler);

        var file = await new BootstrapService(http, temp.Path).GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
        Assert.True(File.Exists(Path.Combine(temp.Path, "dns.json")));
    }

    [Fact]
    public async Task Fresh_cache_avoids_network()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "dns.json"), Fixture.Read("bootstrap-dns.json"), Ct);
        var handler = new FakeHandler { FailUnknown = true };
        using var http = new HttpClient(handler);

        var file = await new BootstrapService(http, temp.Path).GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stale_cache_is_refreshed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "dns.json");
        await File.WriteAllTextAsync(path, """{"services":[]}""", Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        using var http = new HttpClient(new FakeHandler().WithBootstrap());

        var file = await new BootstrapService(http, temp.Path).GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
    }

    [Fact]
    public async Task Force_refresh_ignores_fresh_cache()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "dns.json"), """{"services":[]}""", Ct);
        var handler = new FakeHandler().WithBootstrap();
        using var http = new HttpClient(handler);

        var file = await new BootstrapService(http, temp.Path) { ForceRefresh = true }.GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Network_failure_falls_back_to_stale_cache_with_warning()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "dns.json");
        await File.WriteAllTextAsync(path, Fixture.Read("bootstrap-dns.json"), Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        using var http = new HttpClient(new FakeHandler { FailUnknown = true });
        var service = new BootstrapService(http, temp.Path);
        var warnings = new List<string>();
        service.Warning += warnings.Add;

        var file = await service.GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
        Assert.Contains(warnings, w => w.Contains("cached copy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Network_failure_without_cache_uses_embedded_snapshot_or_throws()
    {
        using var http = new HttpClient(new FakeHandler { FailUnknown = true });
        var service = new BootstrapService(http, cacheDirectory: null);

        if (BootstrapService.ReadEmbedded("dns.json") is null)
        {
            // Built without network access: no snapshot was embedded.
            await Assert.ThrowsAsync<RdapException>(() => service.GetFileAsync(BootstrapKind.Dns, Ct));
        }
        else
        {
            var file = await service.GetFileAsync(BootstrapKind.Dns, Ct);
            Assert.NotEmpty(file.Services);
        }
    }

    [Fact]
    public async Task Corrupt_download_is_not_cached()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new FakeHandler().Json("https://data.iana.org/rdap/dns.json", "<html>oops</html>"));
        var service = new BootstrapService(http, temp.Path);

        try
        {
            await service.GetFileAsync(BootstrapKind.Dns, Ct);
        }
        catch (RdapException)
        {
            // Expected when no embedded snapshot exists.
        }

        Assert.False(File.Exists(Path.Combine(temp.Path, "dns.json")));
    }

    [Fact]
    public async Task Network_disabled_uses_stale_cache_without_downloading_or_warning()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "dns.json");
        await File.WriteAllTextAsync(path, Fixture.Read("bootstrap-dns.json"), Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        var handler = new FakeHandler().WithBootstrap();
        using var http = new HttpClient(handler);
        var service = new BootstrapService(http, temp.Path) { AllowNetwork = false };
        var warnings = new List<string>();
        service.Warning += warnings.Add;

        var file = await service.GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
        Assert.Empty(handler.Requests);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Network_disabled_without_cache_uses_embedded_snapshot_or_explains()
    {
        var handler = new FakeHandler().WithBootstrap();
        using var http = new HttpClient(handler);
        var service = new BootstrapService(http, cacheDirectory: null) { AllowNetwork = false };

        if (BootstrapService.ReadEmbedded("dns.json") is null)
        {
            var ex = await Assert.ThrowsAsync<RdapException>(() => service.GetFileAsync(BootstrapKind.Dns, Ct));
            Assert.Contains("built-in snapshot", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEmpty((await service.GetFileAsync(BootstrapKind.Dns, Ct)).Services);
        }

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Logger_reports_where_each_file_came_from()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new FakeHandler().WithBootstrap());
        var log = new ListLogger();

        await new BootstrapService(http, temp.Path) { Logger = log }.GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.Contains(log.Messages, m => m.Contains("downloading https://data.iana.org/rdap/dns.json", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.Contains("downloaded", StringComparison.Ordinal) && m.Contains(" ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Corrupt_fresh_cache_is_ignored_and_replaced()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "dns.json");
        await File.WriteAllTextAsync(path, "{ truncated", Ct);
        var handler = new FakeHandler().WithBootstrap();
        using var http = new HttpClient(handler);
        var log = new ListLogger();

        var file = await new BootstrapService(http, temp.Path) { Logger = log }.GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
        Assert.Single(handler.Requests);
        Assert.Contains(log.Messages, m => m.Contains("unreadable or corrupt", StringComparison.Ordinal));
        Assert.NotEmpty(BootstrapFile.Parse(await File.ReadAllTextAsync(path, Ct)).Services);
    }

    [Fact]
    public async Task Unwritable_cache_warns_but_still_returns_the_download()
    {
        using var temp = new TempDirectory();
        // A file where the cache directory should be makes Directory.CreateDirectory fail.
        var blocked = Path.Combine(temp.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "", Ct);
        using var http = new HttpClient(new FakeHandler().WithBootstrap());
        var service = new BootstrapService(http, blocked);
        var warnings = new List<string>();
        service.Warning += warnings.Add;

        var file = await service.GetFileAsync(BootstrapKind.Dns, Ct);

        Assert.NotEmpty(file.Services);
        Assert.Contains(warnings, w => w.StartsWith("Could not write bootstrap cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_file_is_loaded_once_per_service()
    {
        var handler = new FakeHandler().WithBootstrap();
        using var http = new HttpClient(handler);
        var service = new BootstrapService(http, cacheDirectory: null);

        var first = await service.GetFileAsync(BootstrapKind.Asn, Ct);
        var second = await service.GetFileAsync(BootstrapKind.Asn, Ct);

        Assert.Same(first, second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Registry_contains_only_the_requested_files()
    {
        var handler = new FakeHandler().WithBootstrap();
        using var http = new HttpClient(handler);

        var registry = await new BootstrapService(http, cacheDirectory: null).GetRegistryAsync([BootstrapKind.Asn], Ct);

        Assert.NotNull(registry.FindAsnServer(15169));
        Assert.Null(registry.FindDomainServer("google.com"));
        Assert.Equal(["https://data.iana.org/rdap/asn.json"], handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void File_names_match_iana()
    {
        Assert.Equal("dns.json", BootstrapService.FileName(BootstrapKind.Dns));
        Assert.Equal("ipv4.json", BootstrapService.FileName(BootstrapKind.Ipv4));
        Assert.Equal("ipv6.json", BootstrapService.FileName(BootstrapKind.Ipv6));
        Assert.Equal("asn.json", BootstrapService.FileName(BootstrapKind.Asn));
        Assert.Equal("object-tags.json", BootstrapService.FileName(BootstrapKind.ObjectTags));
        Assert.Throws<ArgumentOutOfRangeException>(() => BootstrapService.FileName((BootstrapKind)99));
    }
}


/// <summary>Mutates XDG_CACHE_HOME, so it must not run alongside anything else reading it.</summary>
[CollectionDefinition(nameof(CacheDirectoryTests), DisableParallelization = true)]
[Collection(nameof(CacheDirectoryTests))]
public class CacheDirectoryTests
{
    [Fact]
    public void Default_cache_directory_follows_the_platform_convention()
    {
        if (OperatingSystem.IsWindows())
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rdap", "cache");
            Assert.Equal(expected, BootstrapService.DefaultCacheDirectory());
            return;
        }

        var original = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", "/srv/xdg-cache");
            Assert.Equal("/srv/xdg-cache/rdap", BootstrapService.DefaultCacheDirectory());

            // The XDG spec says relative paths are invalid and must be ignored.
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "rdap");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", "relative/cache");
            Assert.Equal(home, BootstrapService.DefaultCacheDirectory());

            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", null);
            Assert.Equal(home, BootstrapService.DefaultCacheDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", original);
        }
    }
}
