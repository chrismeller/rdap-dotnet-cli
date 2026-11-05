using System.Globalization;
using Rdap.Models;

namespace Rdap.Cli;

/// <summary>Renders RDAP objects as whois-style "Key: value" text.</summary>
internal sealed class TextRenderer
{
    private const int KeyWidth = 22;

    private readonly TextWriter _out;
    private readonly bool _color;
    private readonly bool _verbose;
    private int _indent;

    public TextRenderer(TextWriter output, bool color, bool verbose)
    {
        _out = output;
        _color = color;
        _verbose = verbose;
    }

    public void Render(RdapObject obj)
    {
        switch (obj)
        {
            case Domain d: RenderDomain(d); break;
            case Nameserver ns: RenderNameserver(ns); break;
            case IpNetwork net: RenderNetwork(net); break;
            case Autnum asn: RenderAutnum(asn); break;
            case Entity e: RenderEntityBody(e); break;
            default: RenderCommon(obj); break;
        }

        RenderEntities(obj.Entities);
        RenderRemarks(obj.Remarks);
        RenderRedactions(obj.Redacted);

        if (_verbose)
        {
            RenderLinks(obj.Links);
            RenderNotices(obj.Notices);
        }
    }

    public void Header(string text)
    {
        _out.WriteLine(_color ? $"{Ansi.Bold}{Ansi.Yellow}{text}{Ansi.Reset}" : text);
    }

    private void RenderDomain(Domain d)
    {
        Field("Domain Name", d.LdhName ?? d.UnicodeName);
        if (d.UnicodeName is { } u && !string.Equals(u, d.LdhName, StringComparison.OrdinalIgnoreCase))
        {
            Field("Unicode Name", u);
        }

        RenderCommon(d);

        foreach (var ns in d.Nameservers ?? [])
        {
            Field("Name Server", ns.LdhName ?? ns.UnicodeName);
        }

        if (d.SecureDns is { } dnssec)
        {
            var signed = dnssec.DelegationSigned ?? (dnssec.DsData?.Count > 0 || dnssec.KeyData?.Count > 0);
            Field("DNSSEC", signed ? "signed delegation" : "unsigned");
            foreach (var ds in dnssec.DsData ?? [])
            {
                Field("DS Record", $"{ds.KeyTag} {ds.Algorithm} {ds.DigestType} {ds.Digest}");
            }
        }
    }

    private void RenderNameserver(Nameserver ns)
    {
        Field("Name Server", ns.LdhName ?? ns.UnicodeName);
        RenderCommon(ns);
        foreach (var ip in (ns.IpAddresses?.V4 ?? []).Concat(ns.IpAddresses?.V6 ?? []))
        {
            Field("IP Address", ip);
        }
    }

    private void RenderNetwork(IpNetwork net)
    {
        Field("Network Name", net.Name);
        Field("Range", net.StartAddress is not null || net.EndAddress is not null ? $"{net.StartAddress} - {net.EndAddress}" : null);
        foreach (var cidr in net.Cidrs ?? [])
        {
            Field("CIDR", cidr.ToString());
        }

        Field("IP Version", net.IpVersion);
        Field("Type", net.Type);
        Field("Country", net.Country);
        Field("Parent Handle", net.ParentHandle);
        RenderCommon(net);
    }

    private void RenderAutnum(Autnum asn)
    {
        Field("AS Number", asn.StartAutnum switch
        {
            null => null,
            { } s when asn.EndAutnum is null || asn.EndAutnum == s => $"AS{s}",
            { } s => $"AS{s} - AS{asn.EndAutnum}",
        });
        Field("AS Name", asn.Name);
        Field("Type", asn.Type);
        Field("Country", asn.Country);
        RenderCommon(asn);
    }

    /// <summary>Handle, status, events and port 43 — present on every object class.</summary>
    private void RenderCommon(RdapObject obj)
    {
        Field("Handle", obj.Handle);
        foreach (var status in obj.Status ?? [])
        {
            Field("Status", status);
        }

        foreach (var id in obj.PublicIds ?? [])
        {
            Field(id.Type ?? "Public ID", id.Identifier);
        }

        foreach (var ev in obj.Events ?? [])
        {
            var value = FormatDate(ev.EventDate);
            if (ev.EventActor is { } actor)
            {
                value += $" (by {actor})";
            }

            Field(EventLabel(ev.EventAction), value);
        }

        Field("WHOIS Server", obj.Port43);
    }

    private void RenderEntityBody(Entity e)
    {
        if (e.VCard is { } card)
        {
            Field("Name", card.FormattedName);
            if (card.Organization is { } org && org != card.FormattedName)
            {
                Field("Organization", org);
            }

            Field("Kind", card.Kind);
            Field("Title", card.Title);
            Field("Role", card.Role);
            foreach (var address in card.Addresses)
            {
                var lines = address.Lines;
                for (var i = 0; i < lines.Count; i++)
                {
                    Field(i == 0 ? "Address" : "", lines[i]);
                }
            }

            foreach (var email in card.Emails)
            {
                Field("Email", email);
            }

            foreach (var phone in card.Phones)
            {
                Field(phone.IsFax ? "Fax" : "Phone", phone.Number);
            }

            foreach (var url in card.Urls)
            {
                Field("URL", url);
            }
        }

        RenderCommon(e);

        foreach (var net in e.Networks ?? [])
        {
            Field("Network", $"{net.Handle} {net.StartAddress} - {net.EndAddress}".Trim());
        }

        foreach (var asn in e.Autnums ?? [])
        {
            Field("Autnum", $"{asn.Handle} AS{asn.StartAutnum}".Trim());
        }
    }

    private void RenderEntities(List<Entity>? entities)
    {
        foreach (var entity in entities ?? [])
        {
            _out.WriteLine();
            var roles = entity.Roles is { Count: > 0 } r
                ? string.Join(", ", r.Select(TitleCase))
                : "Contact";
            Line(_color ? $"{Ansi.Bold}{roles}:{Ansi.Reset}" : roles + ":");
            _indent += 2;
            RenderEntityBody(entity);
            RenderEntities(entity.Entities);
            RenderRemarks(entity.Remarks);
            _indent -= 2;
        }
    }

    private void RenderRemarks(List<Notice>? remarks)
    {
        foreach (var remark in remarks ?? [])
        {
            RenderNotice("Remark", remark);
        }
    }

    private void RenderNotices(List<Notice>? notices)
    {
        foreach (var notice in notices ?? [])
        {
            RenderNotice("Notice", notice);
        }
    }

    private void RenderNotice(string kind, Notice notice)
    {
        _out.WriteLine();
        Line(_color ? $"{Ansi.Bold}{kind}: {notice.Title}{Ansi.Reset}" : $"{kind}: {notice.Title}");
        foreach (var line in notice.Description ?? [])
        {
            Line("  " + line);
        }

        foreach (var link in notice.Links ?? [])
        {
            Line("  " + link.Href);
        }
    }

    private void RenderRedactions(List<Redaction>? redactions)
    {
        if (redactions is not { Count: > 0 })
        {
            return;
        }

        _out.WriteLine();
        foreach (var redaction in redactions)
        {
            var what = redaction.Name?.ToString() ?? redaction.PrePath ?? redaction.PostPath ?? "(unnamed field)";
            Field("Redacted", redaction.Reason?.ToString() is { } reason ? $"{what} ({reason})" : what);
        }
    }

    private void RenderLinks(List<Link>? links)
    {
        if (links is not { Count: > 0 })
        {
            return;
        }

        _out.WriteLine();
        foreach (var link in links)
        {
            Field("Link", link.Rel is null ? link.Href : $"{link.Href} ({link.Rel})");
        }
    }

    private void Field(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var label = key.Length == 0 ? "" : key + ":";
        var padded = label.PadRight(Math.Max(KeyWidth - _indent, label.Length + 1));
        Line(_color && label.Length > 0 ? $"{Ansi.Cyan}{padded}{Ansi.Reset}{value}" : padded + value);
    }

    private void Line(string text) => _out.WriteLine(new string(' ', _indent) + text);

    internal static string EventLabel(string? action) => action?.ToLowerInvariant() switch
    {
        "registration" => "Registered",
        "expiration" => "Expires",
        "last changed" => "Last Changed",
        "last update of rdap database" => "Database Updated",
        "transfer" => "Transferred",
        "reregistration" => "Re-registered",
        "deletion" => "Deleted",
        null or "" => "Event",
        var other => TitleCase(other),
    };

    internal static string FormatDate(string? value)
    {
        if (value is null)
        {
            return "";
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : value;
    }

    private static string TitleCase(string text) =>
        text.Length == 0 ? text : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
