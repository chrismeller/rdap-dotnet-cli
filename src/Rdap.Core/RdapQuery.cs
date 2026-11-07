using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Rdap;

public enum QueryType
{
    Domain,
    Nameserver,
    Ip,
    Autnum,
    Entity,
}

/// <summary>A classified, normalised RDAP lookup (RFC 9082 path segments).</summary>
public sealed partial record RdapQuery
{
    private RdapQuery(QueryType type, string value, string original)
    {
        Type = type;
        Value = value;
        Original = original;
    }

    public QueryType Type { get; }

    /// <summary>The normalised value used in the URL (A-label domain, canonical IP/CIDR, numeric ASN, ...).</summary>
    public string Value { get; }

    /// <summary>What the user typed.</summary>
    public string Original { get; }

    /// <summary>For <see cref="QueryType.Ip"/>: the address used for bootstrap lookups.</summary>
    public IPAddress? Address { get; private init; }

    /// <summary>For <see cref="QueryType.Autnum"/>: the AS number.</summary>
    public long? AsNumber { get; private init; }

    /// <summary>The RFC 9082 relative path, e.g. <c>domain/example.com</c>.</summary>
    public string Path => Type switch
    {
        QueryType.Domain => "domain/" + Value,
        QueryType.Nameserver => "nameserver/" + Value,
        QueryType.Ip => "ip/" + Value,
        QueryType.Autnum => "autnum/" + Value,
        QueryType.Entity => "entity/" + Uri.EscapeDataString(Value),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Classifies the input automatically, the way <c>whois</c> guesses.</summary>
    /// <exception cref="ArgumentException">The input can't be interpreted.</exception>
    public static RdapQuery Parse(string input) => Parse(input, null);

    /// <summary>Classifies the input, optionally forcing a type.</summary>
    /// <exception cref="ArgumentException">The input is not valid for the (detected or forced) type.</exception>
    public static RdapQuery Parse(string input, QueryType? forcedType)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Query is empty.", nameof(input));
        }

        var type = forcedType ?? Detect(text);
        return type switch
        {
            QueryType.Autnum => ParseAutnum(text, input),
            QueryType.Ip => ParseIp(text, input),
            QueryType.Domain or QueryType.Nameserver => new RdapQuery(type, ToAsciiName(text), input),
            QueryType.Entity => new RdapQuery(type, text, input),
            _ => throw new ArgumentOutOfRangeException(nameof(forcedType)),
        };
    }

    /// <summary>The full stop and its UTS #46 equivalents: ideographic, fullwidth and halfwidth ideographic.</summary>
    private static readonly System.Buffers.SearchValues<char> LabelSeparators = System.Buffers.SearchValues.Create(".。．｡");

    private static QueryType Detect(string text)
    {
        if (AsnPattern().IsMatch(text))
        {
            return QueryType.Autnum;
        }

        if (LooksLikeIp(text))
        {
            return QueryType.Ip;
        }

        // Anything with a dot is a domain; dot-less tokens are entity handles (e.g. GOGL, ABC123-ARIN).
        return text.AsSpan().IndexOfAny(LabelSeparators) >= 0
            ? QueryType.Domain
            : QueryType.Entity;
    }

    private static bool LooksLikeIp(string text)
    {
        var address = text.Split('/', 2)[0];
        // IPAddress.TryParse accepts things like "1.2" and "12345"; only treat dotted quads and colon forms as IPs.
        return (address.Contains(':', StringComparison.Ordinal) || DottedQuadPattern().IsMatch(address)) &&
               IPAddress.TryParse(address, out _);
    }

    private static RdapQuery ParseAutnum(string text, string original)
    {
        var match = AsnPattern().Match(text);
        if (!match.Success ||
            !long.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var asn) ||
            asn > uint.MaxValue)
        {
            throw new ArgumentException($"'{original}' is not a valid AS number.", nameof(original));
        }

        return new RdapQuery(QueryType.Autnum, asn.ToString(CultureInfo.InvariantCulture), original) { AsNumber = asn };
    }

    private static RdapQuery ParseIp(string text, string original)
    {
        var parts = text.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address))
        {
            throw new ArgumentException($"'{original}' is not a valid IP address.", nameof(original));
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            address = new IPAddress(address.GetAddressBytes()); // drop "%eth0"-style zone IDs
        }

        var canonical = address.ToString();

        if (parts.Length == 1)
        {
            return new RdapQuery(QueryType.Ip, canonical, original) { Address = address };
        }

        var maxLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > maxLength)
        {
            throw new ArgumentException($"'{original}' has an invalid prefix length.", nameof(original));
        }

        // Normalise to the network address so "192.0.2.5/24" queries "192.0.2.0/24".
        var network = new IPNetwork(MaskAddress(address, length), length);
        return new RdapQuery(QueryType.Ip, $"{network.BaseAddress}/{length}", original) { Address = network.BaseAddress };
    }

    private static IPAddress MaskAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsInByte = Math.Clamp(prefixLength - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsInByte));
        }

        return new IPAddress(bytes);
    }

    /// <summary>Converts a (possibly internationalised) name to lower-case A-labels.</summary>
    private static string ToAsciiName(string text)
    {
        // Treat ideographic full stops as dots (UTS #46), and drop the root dot.
        var name = text.Replace('。', '.').Replace('．', '.').Replace('｡', '.').TrimEnd('.');
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{text}' is not a valid domain name.", nameof(text));
        }

        try
        {
            return new IdnMapping { UseStd3AsciiRules = false }.GetAscii(name).ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{text}' is not a valid domain name: {ex.Message}", nameof(text), ex);
        }
    }

    [GeneratedRegex(@"^(?:AS)?(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AsnPattern();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedQuadPattern();
}
