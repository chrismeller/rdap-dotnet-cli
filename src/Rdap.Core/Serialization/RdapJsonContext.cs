using System.Text.Json.Serialization;
using Rdap.Bootstrap;
using Rdap.Models;

namespace Rdap.Serialization;

[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Domain))]
[JsonSerializable(typeof(Nameserver))]
[JsonSerializable(typeof(Entity))]
[JsonSerializable(typeof(IpNetwork))]
[JsonSerializable(typeof(Autnum))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(RdapObject))]
[JsonSerializable(typeof(BootstrapFile))]
internal sealed partial class RdapJsonContext : JsonSerializerContext;
