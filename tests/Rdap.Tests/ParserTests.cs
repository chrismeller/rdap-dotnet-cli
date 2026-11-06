using Rdap.Models;
using Rdap.Serialization;

namespace Rdap.Tests;

public class ParserTests
{
    [Fact]
    public void Parses_registry_domain()
    {
        var domain = Assert.IsType<Domain>(RdapResponseParser.Parse(Fixture.Read("domain-verisign-google.json")));

        Assert.Equal("GOOGLE.COM", domain.LdhName);
        Assert.Equal("2138514_DOMAIN_COM-VRSN", domain.Handle);
        Assert.Equal(4, domain.Nameservers!.Count);
        Assert.Contains("client transfer prohibited", domain.Status!);
        Assert.False(domain.SecureDns!.DelegationSigned);
        Assert.Contains(domain.Events!, e => e.EventAction == "registration" && e.EventDate == "1997-09-15T04:00:00Z");

        var registrar = Assert.Single(domain.Entities!);
        Assert.Equal(["registrar"], registrar.Roles);
        Assert.Equal("MarkMonitor Inc.", registrar.VCard!.FormattedName);
        Assert.Equal("292", Assert.Single(registrar.PublicIds!).Identifier);

        var abuse = Assert.Single(registrar.Entities!);
        Assert.Equal("abusecomplaints@markmonitor.com", Assert.Single(abuse.VCard!.Emails));
        Assert.Equal("+1.2086851750", Assert.Single(abuse.VCard.Phones).Number);
    }

    [Fact]
    public void Parses_registrar_domain_with_redactions()
    {
        var domain = Assert.IsType<Domain>(RdapResponseParser.Parse(Fixture.Read("domain-markmonitor-google.json")));

        Assert.Equal(2, domain.Redacted!.Count);
        Assert.Equal("Registrant Name", domain.Redacted[0].Name!.ToString());
        Assert.Equal("Server policy", domain.Redacted[0].Reason!.ToString());
        Assert.Equal("removal", domain.Redacted[1].Method);

        var registrant = domain.Entities!.Single(e => e.Roles!.Contains("registrant"));
        Assert.Equal("Google LLC", registrant.VCard!.Organization);
        Assert.Equal(["CA", "US"], Assert.Single(registrant.VCard.Addresses).Lines);

        var registrar = domain.Entities!.Single(e => e.Roles!.Contains("registrar"));
        Assert.Equal(["3540 East Longwing Lane, Suite 300", "Meridian, ID 83646", "US"], Assert.Single(registrar.VCard!.Addresses).Lines);

        var tech = domain.Entities!.Single(e => e.Roles!.Contains("technical"));
        Assert.Collection(tech.VCard!.Phones,
            p => Assert.False(p.IsFax),
            p => Assert.True(p.IsFax));
    }

    [Fact]
    public void Parses_ip_network()
    {
        var net = Assert.IsType<IpNetwork>(RdapResponseParser.Parse(Fixture.Read("ip-arin-8.8.8.8.json")));

        Assert.Equal("NET-8-8-8-0-2", net.Handle);
        Assert.Equal("8.8.8.0", net.StartAddress);
        Assert.Equal("8.8.8.255", net.EndAddress);
        Assert.Equal("8.8.8.0/24", Assert.Single(net.Cidrs!).ToString());
        Assert.Equal("whois.arin.net", net.Port43);

        var org = Assert.Single(net.Entities!);
        Assert.Equal("org", org.VCard!.Kind);
        Assert.Equal(["1600 Amphitheatre Parkway", "Mountain View", "CA", "94043", "United States"], Assert.Single(org.VCard.Addresses).Lines);
        Assert.Equal("+1-650-253-0000", Assert.Single(org.Entities!).VCard!.Phones.Single().Number);
    }

    [Fact]
    public void Parses_autnum_with_null_address_value()
    {
        var asn = Assert.IsType<Autnum>(RdapResponseParser.Parse(Fixture.Read("autnum-ripe-3333.json")));

        Assert.Equal(3333, asn.StartAutnum);
        Assert.Equal("RIPE-NCC-AS", asn.Name);
        var person = asn.Entities!.Single(e => e.Handle == "GII-RIPE");
        Assert.Equal("Amsterdam", person.VCard!.Addresses.Single().Lines[2]);
    }

    [Fact]
    public void Parses_nameserver_and_entity()
    {
        var ns = Assert.IsType<Nameserver>(RdapResponseParser.Parse(Fixture.Read("nameserver-verisign.json")));
        Assert.Equal(["216.239.32.10"], ns.IpAddresses!.V4);
        Assert.Equal(["2001:4860:4802:32::a"], ns.IpAddresses.V6);

        var entity = Assert.IsType<Entity>(RdapResponseParser.Parse(Fixture.Read("entity-arin-gogl.json")));
        Assert.Equal("Google LLC", entity.VCard!.FormattedName);
        Assert.Single(entity.Networks!);
        Assert.Equal(15169, Assert.Single(entity.Autnums!).StartAutnum);
    }

    [Fact]
    public void Tolerates_numbers_as_strings_and_unknown_members()
    {
        const string json = """
            {"objectClassName":"autnum","startAutnum":"64496","endAutnum":"64511","someVendor_extension":{"x":[1,2]}}
            """;

        var asn = Assert.IsType<Autnum>(RdapResponseParser.Parse(json));

        Assert.Equal(64496, asn.StartAutnum);
        Assert.Equal(64511, asn.EndAutnum);
    }

    [Fact]
    public void Unknown_object_class_parses_as_generic_object()
    {
        var obj = RdapResponseParser.Parse("""{"objectClassName":"something new","handle":"X"}""");

        Assert.Equal("X", obj.Handle);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"handle":"no object class"}""")]
    public void Rejects_non_rdap(string json) =>
        Assert.Throws<RdapParseException>(() => RdapResponseParser.Parse(json));

    [Fact]
    public void Shape_mismatch_reports_json_path()
    {
        var ex = Assert.Throws<RdapParseException>(() =>
            RdapResponseParser.Parse("""{"objectClassName":"domain","nameservers":{"oops":"object"}}"""));

        Assert.Contains("$.nameservers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_error_response()
    {
        var error = RdapResponseParser.TryParseError(Fixture.Read("error-404.json"));

        Assert.Equal(404, error!.ErrorCode);
        Assert.Equal("Not Found", error.Title);
    }
}

public class VCardTests
{
    private static VCard? Parse(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return VCard.Parse(doc.RootElement.Clone());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"vcard\"")]
    [InlineData("[\"vcard\"]")]
    [InlineData("[\"vcard\", {}]")]
    public void Non_jcard_returns_null(string json) => Assert.Null(Parse(json));

    [Fact]
    public void Skips_malformed_properties()
    {
        var card = Parse("""["vcard", [["fn"], 42, ["fn", {}, "text", "Real Name"], [1, {}, "text", "x"]]]""");

        Assert.Equal("Real Name", card!.FormattedName);
    }

    [Fact]
    public void Handles_multi_line_street_and_structured_org()
    {
        var card = Parse("""
            ["vcard", [
              ["org", {}, "text", ["Example Inc.", "Networking"]],
              ["adr", {}, "text", ["", "Suite 5", ["1 Main St", "Building B"], "Springfield", "", "12345", "US"]],
              ["email", {}, "text", "a@example.com"],
              ["email", {}, "text", "b@example.com"],
              ["url", {}, "uri", "https://example.com"]
            ]]
            """);

        Assert.Equal("Example Inc., Networking", card!.Organization);
        Assert.Equal(["Suite 5", "1 Main St", "Building B", "Springfield, 12345", "US"], card.Addresses.Single().Lines);
        Assert.Equal(["a@example.com", "b@example.com"], card.Emails);
        Assert.Equal(["https://example.com"], card.Urls);
    }

    [Fact]
    public void Empty_address_is_omitted()
    {
        var card = Parse("""["vcard", [["adr", {}, "text", ["", "", "", "", "", "", ""]]]]""");

        Assert.Empty(card!.Addresses);
    }
}
