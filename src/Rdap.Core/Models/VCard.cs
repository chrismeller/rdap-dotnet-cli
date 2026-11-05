using System.Text.Json;

namespace Rdap.Models;

/// <summary>A readable projection of an RFC 7095 jCard as used in RDAP entities.</summary>
public sealed class VCard
{
    public string? FormattedName { get; private set; }
    public string? Kind { get; private set; }
    public string? Organization { get; private set; }
    public string? Title { get; private set; }
    public string? Role { get; private set; }
    public List<string> Emails { get; } = [];
    public List<VCardPhone> Phones { get; } = [];
    public List<VCardAddress> Addresses { get; } = [];
    public List<string> Urls { get; } = [];

    /// <summary>Parses a jCard array (<c>["vcard", [ [name, params, type, value...], ... ]]</c>).</summary>
    /// <returns>The parsed card, or null if the element is not a jCard.</returns>
    public static VCard? Parse(JsonElement jcard)
    {
        if (jcard.ValueKind != JsonValueKind.Array || jcard.GetArrayLength() < 2)
        {
            return null;
        }

        var properties = jcard[1];
        if (properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var card = new VCard();
        foreach (var prop in properties.EnumerateArray())
        {
            if (prop.ValueKind != JsonValueKind.Array || prop.GetArrayLength() < 4 ||
                prop[0].ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = prop[0].GetString()!.ToLowerInvariant();
            var parameters = prop[1];
            var value = prop[3];

            switch (name)
            {
                case "fn":
                    card.FormattedName ??= JoinText(value);
                    break;
                case "kind":
                    card.Kind ??= JoinText(value);
                    break;
                case "org":
                    card.Organization ??= JoinText(value);
                    break;
                case "title":
                    card.Title ??= JoinText(value);
                    break;
                case "role":
                    card.Role ??= JoinText(value);
                    break;
                case "email":
                    AddIfPresent(card.Emails, JoinText(value));
                    break;
                case "url":
                    AddIfPresent(card.Urls, JoinText(value));
                    break;
                case "tel":
                    var number = JoinText(value);
                    if (!string.IsNullOrWhiteSpace(number))
                    {
                        if (number.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                        {
                            number = number[4..];
                        }

                        card.Phones.Add(new VCardPhone(number, GetTypes(parameters)));
                    }

                    break;
                case "adr":
                    var address = ParseAddress(parameters, value);
                    if (address.Lines.Count > 0)
                    {
                        card.Addresses.Add(address);
                    }

                    break;
            }
        }

        return card;
    }

    private static void AddIfPresent(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            list.Add(value);
        }
    }

    private static VCardAddress ParseAddress(JsonElement parameters, JsonElement value)
    {
        // Structured value: [pobox, ext, street, locality, region, code, country], where any
        // component may itself be an array (e.g. multiple street lines). Fall back to the
        // free-form "label" parameter when the structured value is empty.
        var lines = new List<string>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            var components = value.EnumerateArray().Select(ComponentLines).ToArray();
            string? Single(int i) => i < components.Length && components[i].Count > 0 ? string.Join(" ", components[i]) : null;

            for (var i = 0; i <= 2 && i < components.Length; i++)
            {
                lines.AddRange(components[i]);
            }

            var locality = Single(3);
            var regionAndCode = string.Join(" ", new[] { Single(4), Single(5) }.Where(s => s is not null));
            var cityLine = (locality, regionAndCode.Length > 0) switch
            {
                ({ } l, true) => $"{l}, {regionAndCode}",
                ({ } l, false) => l,
                (null, _) => regionAndCode,
            };
            if (cityLine.Length > 0)
            {
                lines.Add(cityLine);
            }

            if (Single(6) is { } country)
            {
                lines.Add(country);
            }
        }

        if (lines.Count == 0 && parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
        {
            lines.AddRange(SplitLines(label.GetString()!));
        }

        string? cc = null;
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cc", out var ccEl) &&
            ccEl.ValueKind == JsonValueKind.String)
        {
            cc = ccEl.GetString();
        }

        return new VCardAddress(lines, cc);
    }

    private static List<string> ComponentLines(JsonElement component) => component.ValueKind switch
    {
        JsonValueKind.String => SplitLines(component.GetString()!),
        JsonValueKind.Array => component.EnumerateArray().SelectMany(ComponentLines).ToList(),
        _ => [],
    };

    private static List<string> SplitLines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<string> GetTypes(JsonElement parameters)
    {
        var types = new List<string>();
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                types.Add(type.GetString()!);
            }
            else if (type.ValueKind == JsonValueKind.Array)
            {
                types.AddRange(type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!));
            }
        }

        return types;
    }

    /// <summary>Flattens a jCard value (string, number, or nested array of them) into text.</summary>
    private static string? JoinText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(JoinText).Where(s => !string.IsNullOrWhiteSpace(s))),
        _ => null,
    };
}

public sealed record VCardPhone(string Number, IReadOnlyList<string> Types)
{
    public bool IsFax => Types.Any(t => t.Equals("fax", StringComparison.OrdinalIgnoreCase));
}

public sealed record VCardAddress(IReadOnlyList<string> Lines, string? CountryCode);
