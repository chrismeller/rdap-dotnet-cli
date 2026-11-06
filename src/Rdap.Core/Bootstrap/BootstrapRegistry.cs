using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rdap.Serialization;

namespace Rdap.Bootstrap;

/// <summary>The kinds of IANA bootstrap registry (RFC 9224 and RFC 8521).</summary>
public enum BootstrapKind
{
    Dns,
    Ipv4,
    Ipv6,
    Asn,
    ObjectTags,
}

/// <summary>Wire format shared by all IANA RDAP bootstrap files.</summary>
public sealed class BootstrapFile
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("publication")] public string? Publication { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>
    /// Each service is <c>[entries, urls]</c>, or <c>[contacts, entries, urls]</c> for object tags.
    /// </summary>
    [JsonPropertyName("services")] public List<List<List<string>>> Services { get; set; } = [];

    public static BootstrapFile Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RdapJsonContext.Default.BootstrapFile)
                   ?? throw new RdapParseException("Bootstrap file deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new RdapParseException($"Invalid bootstrap file: {ex.Message}", ex);
        }
    }

    /// <summary>Yields (entries, urls) pairs, normalising the object-tags 3-element form.</summary>
    internal IEnumerable<(List<string> Entries, List<string> Urls)> Entries()
    {
        foreach (var service in Services)
        {
            if (service.Count >= 2)
            {
                yield return (service[^2], service[^1]);
            }
        }
    }
}

/// <summary>Resolves the authoritative RDAP base URL for a query using the IANA bootstrap data.</summary>
public sealed class BootstrapRegistry
{
    private readonly List<(string Suffix, Uri Server)> _dns = [];
    private readonly List<(IPNetwork Network, Uri Server)> _ip = [];
    private readonly List<(long Start, long End, Uri Server)> _asn = [];
    private readonly Dictionary<string, Uri> _tags = new(StringComparer.OrdinalIgnoreCase);

    public BootstrapRegistry(BootstrapFile? dns, BootstrapFile? ipv4, BootstrapFile? ipv6, BootstrapFile? asn, BootstrapFile? objectTags)
    {
        foreach (var (entries, urls) in dns?.Entries() ?? [])
        {
            if (PickUrl(urls) is not { } server) continue;
            foreach (var entry in entries)
            {
                _dns.Add((entry.Trim('.').ToLowerInvariant(), server));
            }
        }

        foreach (var file in new[] { ipv4, ipv6 })
        {
            foreach (var (entries, urls) in file?.Entries() ?? [])
            {
                if (PickUrl(urls) is not { } server) continue;
                foreach (var entry in entries)
                {
                    if (IPNetwork.TryParse(entry, out var network))
                    {
                        _ip.Add((network, server));
                    }
                }
            }
        }

        foreach (var (entries, urls) in asn?.Entries() ?? [])
        {
            if (PickUrl(urls) is not { } server) continue;
            foreach (var entry in entries)
            {
                var parts = entry.Split('-', 2);
                if (long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
                {
                    var end = start;
                    if (parts.Length == 2 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end))
                    {
                        continue;
                    }

                    _asn.Add((start, end, server));
                }
            }
        }

        foreach (var (entries, urls) in objectTags?.Entries() ?? [])
        {
            if (PickUrl(urls) is not { } server) continue;
            foreach (var entry in entries)
            {
                _tags[entry] = server;
            }
        }
    }

    /// <summary>Finds the server for a domain (or nameserver) name in A-label form, by longest matching suffix.</summary>
    public Uri? FindDomainServer(string asciiName)
    {
        var name = asciiName.Trim('.').ToLowerInvariant();
        Uri? best = null;
        var bestLength = -1;
        foreach (var (suffix, server) in _dns)
        {
            var matches = suffix.Length == 0 || name == suffix || name.EndsWith("." + suffix, StringComparison.Ordinal);
            if (matches && suffix.Length > bestLength)
            {
                best = server;
                bestLength = suffix.Length;
            }
        }

        return best;
    }

    /// <summary>Finds the server for an IP address by longest matching prefix.</summary>
    public Uri? FindIpServer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Uri? best = null;
        var bestLength = -1;
        foreach (var (network, server) in _ip)
        {
            if (network.PrefixLength > bestLength && network.Contains(address))
            {
                best = server;
                bestLength = network.PrefixLength;
            }
        }

        return best;
    }

    public Uri? FindAsnServer(long asn)
    {
        foreach (var (start, end, server) in _asn)
        {
            if (asn >= start && asn <= end)
            {
                return server;
            }
        }

        return null;
    }

    /// <summary>Finds the server for an entity handle using its object tag suffix (e.g. <c>XXXX-ARIN</c>), per RFC 8521.</summary>
    public Uri? FindEntityServer(string handle)
    {
        var dash = handle.LastIndexOf('-');
        if (dash < 0 || dash == handle.Length - 1)
        {
            return null;
        }

        return _tags.TryGetValue(handle[(dash + 1)..], out var server) ? server : null;
    }

    /// <summary>Prefers an https URL, and normalises it to end in '/' so relative paths append correctly.</summary>
    private static Uri? PickUrl(List<string> urls)
    {
        var chosen = urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                     ?? urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            return null;
        }

        if (!chosen.EndsWith('/'))
        {
            chosen += "/";
        }

        return Uri.TryCreate(chosen, UriKind.Absolute, out var uri) ? uri : null;
    }
}
