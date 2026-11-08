using Rdap.Cli;
using Rdap.Serialization;

namespace Rdap.Tests;

public sealed class TextRendererTests
{
    private static string Render(string json, bool color = false, bool verbose = false)
    {
        using var output = new StringWriter();
        new TextRenderer(output, color, verbose).Render(RdapResponseParser.Parse(json));
        return output.ToString();
    }

    [Fact]
    public void Domain_shows_unicode_name_and_ds_records()
    {
        var text = Render("""
            {
              "objectClassName": "domain",
              "ldhName": "xn--bcher-kva.example",
              "unicodeName": "bücher.example",
              "secureDNS": {
                "dsData": [{ "keyTag": 12345, "algorithm": 13, "digestType": 2, "digest": "ABCDEF" }]
              }
            }
            """);

        Assert.Matches(@"Domain Name:\s+xn--bcher-kva\.example", text);
        Assert.Matches(@"Unicode Name:\s+bücher\.example", text);
        Assert.Matches(@"DNSSEC:\s+signed delegation", text); // inferred from dsData when delegationSigned is absent
        Assert.Matches(@"DS Record:\s+12345 13 2 ABCDEF", text);
    }

    [Fact]
    public void Unicode_name_matching_ldh_name_is_not_repeated()
    {
        var text = Render("""{ "objectClassName": "domain", "ldhName": "EXAMPLE.COM", "unicodeName": "example.com" }""");

        Assert.DoesNotContain("Unicode Name", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(64496, 64511, "AS64496 - AS64511")]
    [InlineData(64496, 64496, "AS64496")]
    public void Autnum_shows_single_numbers_and_ranges(long start, long end, string expected)
    {
        var text = Render($$"""{ "objectClassName": "autnum", "startAutnum": {{start}}, "endAutnum": {{end}}, "name": "EXAMPLE-AS" }""");

        Assert.Matches($@"AS Number:\s+{expected}(\r?\n)", text);
        Assert.Matches(@"AS Name:\s+EXAMPLE-AS", text);
    }

    [Fact]
    public void Event_actor_is_shown_next_to_the_date()
    {
        var text = Render("""
            {
              "objectClassName": "domain",
              "ldhName": "example.com",
              "events": [{ "eventAction": "transfer", "eventDate": "2024-01-02T03:04:05Z", "eventActor": "REGISTRAR-1" }]
            }
            """);

        Assert.Matches(@"Transferred:\s+2024-01-02 03:04:05 UTC \(by REGISTRAR-1\)", text);
    }

    [Fact]
    public void Entity_vcard_fields_are_rendered()
    {
        var text = Render("""
            {
              "objectClassName": "entity",
              "handle": "EX-1",
              "vcardArray": ["vcard", [
                ["version", {}, "text", "4.0"],
                ["fn", {}, "text", "Jane Doe"],
                ["org", {}, "text", "Example Org"],
                ["kind", {}, "text", "individual"],
                ["title", {}, "text", "Network Engineer"],
                ["role", {}, "text", "Technical Contact"],
                ["url", {}, "uri", "https://example.test/"]
              ]]
            }
            """);

        Assert.Matches(@"Name:\s+Jane Doe", text);
        Assert.Matches(@"Organization:\s+Example Org", text);
        Assert.Matches(@"Kind:\s+individual", text);
        Assert.Matches(@"Title:\s+Network Engineer", text);
        Assert.Matches(@"Role:\s+Technical Contact", text);
        Assert.Matches(@"URL:\s+https://example\.test/", text);
        Assert.Matches(@"Handle:\s+EX-1", text);
    }

    [Fact]
    public void Colour_wraps_labels_and_headings()
    {
        var text = Render("""
            {
              "objectClassName": "domain",
              "ldhName": "example.com",
              "entities": [{ "objectClassName": "entity", "roles": ["registrar"], "handle": "R-1" }],
              "notices": [{ "title": "Terms", "description": ["Be nice."] }]
            }
            """, color: true, verbose: true);

        Assert.Contains("\e[36mDomain Name:", text, StringComparison.Ordinal);
        Assert.Contains("\e[1mRegistrar:\e[0m", text, StringComparison.Ordinal);
        Assert.Contains("\e[1mNotice: Terms\e[0m", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("registration", "Registered")]
    [InlineData("EXPIRATION", "Expires")]
    [InlineData("last changed", "Last Changed")]
    [InlineData("last update of RDAP database", "Database Updated")]
    [InlineData("transfer", "Transferred")]
    [InlineData("reregistration", "Re-registered")]
    [InlineData("deletion", "Deleted")]
    [InlineData("", "Event")]
    [InlineData(null, "Event")]
    [InlineData("enum validation expiration", "Enum Validation Expiration")]
    public void Event_labels(string? action, string expected) =>
        Assert.Equal(expected, TextRenderer.EventLabel(action));

    [Fact]
    public void Missing_date_formats_as_empty() => Assert.Equal("", TextRenderer.FormatDate(null));
}
