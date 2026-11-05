using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rdap.Models;

// Wire models for RFC 9083 (JSON Responses for RDAP). Every member is optional because
// servers vary wildly in what they return; anything not modelled here is still available
// via the raw JSON.

public class RdapResponse
{
    [JsonPropertyName("objectClassName")] public string? ObjectClassName { get; set; }
    [JsonPropertyName("rdapConformance")] public List<string>? RdapConformance { get; set; }
    [JsonPropertyName("notices")] public List<Notice>? Notices { get; set; }
    [JsonPropertyName("lang")] public string? Lang { get; set; }
}

public class RdapObject : RdapResponse
{
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("status")] public List<string>? Status { get; set; }
    [JsonPropertyName("entities")] public List<Entity>? Entities { get; set; }
    [JsonPropertyName("events")] public List<RdapEvent>? Events { get; set; }
    [JsonPropertyName("links")] public List<Link>? Links { get; set; }
    [JsonPropertyName("remarks")] public List<Notice>? Remarks { get; set; }
    [JsonPropertyName("port43")] public string? Port43 { get; set; }
    [JsonPropertyName("publicIds")] public List<PublicId>? PublicIds { get; set; }

    /// <summary>RFC 9537 redaction markers.</summary>
    [JsonPropertyName("redacted")] public List<Redaction>? Redacted { get; set; }
}

public sealed class Domain : RdapObject
{
    [JsonPropertyName("ldhName")] public string? LdhName { get; set; }
    [JsonPropertyName("unicodeName")] public string? UnicodeName { get; set; }
    [JsonPropertyName("nameservers")] public List<Nameserver>? Nameservers { get; set; }
    [JsonPropertyName("secureDNS")] public SecureDns? SecureDns { get; set; }
    [JsonPropertyName("network")] public IpNetwork? Network { get; set; }
}

public sealed class Nameserver : RdapObject
{
    [JsonPropertyName("ldhName")] public string? LdhName { get; set; }
    [JsonPropertyName("unicodeName")] public string? UnicodeName { get; set; }
    [JsonPropertyName("ipAddresses")] public IpAddresses? IpAddresses { get; set; }
}

public sealed class IpAddresses
{
    [JsonPropertyName("v4")] public List<string>? V4 { get; set; }
    [JsonPropertyName("v6")] public List<string>? V6 { get; set; }
}

public sealed class Entity : RdapObject
{
    [JsonPropertyName("vcardArray")] public JsonElement? VCardArray { get; set; }
    [JsonPropertyName("roles")] public List<string>? Roles { get; set; }
    [JsonPropertyName("asEventActor")] public List<RdapEvent>? AsEventActor { get; set; }
    [JsonPropertyName("networks")] public List<IpNetwork>? Networks { get; set; }
    [JsonPropertyName("autnums")] public List<Autnum>? Autnums { get; set; }

    private VCard? _vcard;
    private bool _vcardParsed;

    /// <summary>The parsed jCard, or null if absent/unparseable.</summary>
    [JsonIgnore]
    public VCard? VCard
    {
        get
        {
            if (!_vcardParsed)
            {
                _vcard = VCardArray is { } el ? VCard.Parse(el) : null;
                _vcardParsed = true;
            }

            return _vcard;
        }
    }
}

public sealed class IpNetwork : RdapObject
{
    [JsonPropertyName("startAddress")] public string? StartAddress { get; set; }
    [JsonPropertyName("endAddress")] public string? EndAddress { get; set; }
    [JsonPropertyName("ipVersion")] public string? IpVersion { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("parentHandle")] public string? ParentHandle { get; set; }

    /// <summary>The widely deployed "cidr0" extension.</summary>
    [JsonPropertyName("cidr0_cidrs")] public List<Cidr0>? Cidrs { get; set; }
}

public sealed class Cidr0
{
    [JsonPropertyName("v4prefix")] public string? V4Prefix { get; set; }
    [JsonPropertyName("v6prefix")] public string? V6Prefix { get; set; }
    [JsonPropertyName("length")] public int? Length { get; set; }

    public override string ToString() => $"{V4Prefix ?? V6Prefix}/{Length}";
}

public sealed class Autnum : RdapObject
{
    [JsonPropertyName("startAutnum")] public long? StartAutnum { get; set; }
    [JsonPropertyName("endAutnum")] public long? EndAutnum { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
}

public sealed class ErrorResponse : RdapResponse
{
    [JsonPropertyName("errorCode")] public int? ErrorCode { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public List<string>? Description { get; set; }
}

public sealed class Notice
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("description")] public List<string>? Description { get; set; }
    [JsonPropertyName("links")] public List<Link>? Links { get; set; }
}

public sealed class Link
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("rel")] public string? Rel { get; set; }
    [JsonPropertyName("href")] public string? Href { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public sealed class RdapEvent
{
    [JsonPropertyName("eventAction")] public string? EventAction { get; set; }
    [JsonPropertyName("eventActor")] public string? EventActor { get; set; }

    // Kept as a string: servers emit a variety of not-quite-RFC 3339 formats.
    [JsonPropertyName("eventDate")] public string? EventDate { get; set; }
    [JsonPropertyName("links")] public List<Link>? Links { get; set; }
}

public sealed class PublicId
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("identifier")] public string? Identifier { get; set; }
}

public sealed class SecureDns
{
    [JsonPropertyName("zoneSigned")] public bool? ZoneSigned { get; set; }
    [JsonPropertyName("delegationSigned")] public bool? DelegationSigned { get; set; }
    [JsonPropertyName("maxSigLife")] public long? MaxSigLife { get; set; }
    [JsonPropertyName("dsData")] public List<DsData>? DsData { get; set; }
    [JsonPropertyName("keyData")] public List<KeyData>? KeyData { get; set; }
}

public sealed class DsData
{
    [JsonPropertyName("keyTag")] public long? KeyTag { get; set; }
    [JsonPropertyName("algorithm")] public int? Algorithm { get; set; }
    [JsonPropertyName("digestType")] public int? DigestType { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

public sealed class KeyData
{
    [JsonPropertyName("flags")] public int? Flags { get; set; }
    [JsonPropertyName("protocol")] public int? Protocol { get; set; }
    [JsonPropertyName("algorithm")] public int? Algorithm { get; set; }
    [JsonPropertyName("publicKey")] public string? PublicKey { get; set; }
}

public sealed class Redaction
{
    [JsonPropertyName("name")] public RedactionText? Name { get; set; }
    [JsonPropertyName("reason")] public RedactionText? Reason { get; set; }
    [JsonPropertyName("prePath")] public string? PrePath { get; set; }
    [JsonPropertyName("postPath")] public string? PostPath { get; set; }
    [JsonPropertyName("replacementPath")] public string? ReplacementPath { get; set; }
    [JsonPropertyName("pathLang")] public string? PathLang { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
}

public sealed class RedactionText
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    public override string? ToString() => Description ?? Type;
}
