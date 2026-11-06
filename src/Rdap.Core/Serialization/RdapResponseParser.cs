using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Rdap.Models;

namespace Rdap.Serialization;

/// <summary>Turns raw RDAP JSON into the typed model matching its <c>objectClassName</c>.</summary>
public static class RdapResponseParser
{
    /// <summary>Parses an RDAP object response.</summary>
    /// <exception cref="RdapParseException">The JSON is malformed or doesn't match the RDAP model.</exception>
    public static RdapObject Parse(string json)
    {
        string? objectClass;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RdapParseException("RDAP response is not a JSON object.");
            }

            objectClass = doc.RootElement.TryGetProperty("objectClassName", out var oc) && oc.ValueKind == JsonValueKind.String
                ? oc.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new RdapParseException($"RDAP response is not valid JSON: {ex.Message}", ex);
        }

        var ctx = RdapJsonContext.Default;
        return objectClass?.ToLowerInvariant() switch
        {
            "domain" => Deserialize(json, ctx.Domain),
            "nameserver" => Deserialize(json, ctx.Nameserver),
            "entity" => Deserialize(json, ctx.Entity),
            "ip network" => Deserialize(json, ctx.IpNetwork),
            "autnum" => Deserialize(json, ctx.Autnum),
            null => throw new RdapParseException("RDAP response has no objectClassName."),
            _ => Deserialize(json, ctx.RdapObject),
        };
    }

    /// <summary>Attempts to parse an RFC 9083 error response body; returns null if it isn't one.</summary>
    public static ErrorResponse? TryParseError(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RdapJsonContext.Default.ErrorResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                   ?? throw new RdapParseException("RDAP response deserialized to null.");
        }
        catch (JsonException ex)
        {
            var where = ex.Path is { } path ? $" at {path}" : "";
            throw new RdapParseException($"RDAP response doesn't match the expected {typeof(T).Name} shape{where}: {ex.Message}", ex);
        }
    }
}
