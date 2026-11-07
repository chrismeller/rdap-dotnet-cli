namespace Rdap.Tests;

public class RdapQueryTests
{
    [Theory]
    [InlineData("example.com", QueryType.Domain, "example.com", "domain/example.com")]
    [InlineData("EXAMPLE.COM.", QueryType.Domain, "example.com", "domain/example.com")]
    [InlineData("bücher.de", QueryType.Domain, "xn--bcher-kva.de", "domain/xn--bcher-kva.de")]
    [InlineData("例え。テスト", QueryType.Domain, "xn--r8jz45g.xn--zckzah", "domain/xn--r8jz45g.xn--zckzah")]
    [InlineData("8.8.8.8", QueryType.Ip, "8.8.8.8", "ip/8.8.8.8")]
    [InlineData("192.0.2.77/24", QueryType.Ip, "192.0.2.0/24", "ip/192.0.2.0/24")]
    [InlineData("2001:4860:4860:0:0:0:0:8888", QueryType.Ip, "2001:4860:4860::8888", "ip/2001:4860:4860::8888")]
    [InlineData("2001:db8::1/32", QueryType.Ip, "2001:db8::/32", "ip/2001:db8::/32")]
    [InlineData("::ffff:8.8.4.4", QueryType.Ip, "8.8.4.4", "ip/8.8.4.4")]
    [InlineData("15169", QueryType.Autnum, "15169", "autnum/15169")]
    [InlineData("AS15169", QueryType.Autnum, "15169", "autnum/15169")]
    [InlineData("as3333", QueryType.Autnum, "3333", "autnum/3333")]
    [InlineData("GOGL", QueryType.Entity, "GOGL", "entity/GOGL")]
    [InlineData("ABUSE5250-ARIN", QueryType.Entity, "ABUSE5250-ARIN", "entity/ABUSE5250-ARIN")]
    [InlineData("  example.org  ", QueryType.Domain, "example.org", "domain/example.org")]
    public void Detects_and_normalises(string input, QueryType type, string value, string path)
    {
        var query = RdapQuery.Parse(input);

        Assert.Equal(type, query.Type);
        Assert.Equal(value, query.Value);
        Assert.Equal(path, query.Path);
    }

    [Fact]
    public void Numbers_that_IPAddress_would_accept_are_still_ASNs()
    {
        // IPAddress.TryParse("12345") succeeds (as 0.0.48.57); whois treats it as an ASN.
        Assert.Equal(QueryType.Autnum, RdapQuery.Parse("12345").Type);
    }

    [Fact]
    public void Forced_type_overrides_detection()
    {
        var query = RdapQuery.Parse("ns1.google.com", QueryType.Nameserver);

        Assert.Equal(QueryType.Nameserver, query.Type);
        Assert.Equal("nameserver/ns1.google.com", query.Path);
    }

    [Fact]
    public void Entity_handles_are_escaped_in_path() =>
        Assert.Equal("entity/A%20B", RdapQuery.Parse("A B", QueryType.Entity).Path);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a..b")]
    [InlineData("AS99999999999")]
    public void Rejects_invalid_input(string input) =>
        Assert.Throws<ArgumentException>(() => RdapQuery.Parse(input));

    [Theory]
    [InlineData("8.8.8.8/33")]
    [InlineData("not-an-ip")]
    [InlineData("2001:db8::/129")]
    public void Rejects_invalid_ip_when_forced(string input) =>
        Assert.Throws<ArgumentException>(() => RdapQuery.Parse(input, QueryType.Ip));

    [Fact]
    public void Ipv6_zone_ids_are_dropped() =>
        Assert.Equal("ip/fe80::1", RdapQuery.Parse("fe80::1%3").Path);

    [Fact]
    public void Overlong_domain_labels_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => RdapQuery.Parse(new string('a', 64) + ".com"));

        Assert.Contains("is not a valid domain name", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("example\uFF0Ecom")] // fullwidth full stop
    [InlineData("example\uFF61com")] // halfwidth ideographic full stop
    public void Alternative_full_stops_are_dots(string escaped) =>
        Assert.Equal("domain/example.com", RdapQuery.Parse(System.Text.RegularExpressions.Regex.Unescape(escaped)).Path);
}
