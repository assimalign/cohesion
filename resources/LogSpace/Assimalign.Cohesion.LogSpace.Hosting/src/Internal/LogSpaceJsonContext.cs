using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal sealed record StoredLog(string T, int Sev, string SevText, string Svc, string Cat,
    string Body, Dictionary<string, JsonElement> Attrs);
internal sealed record SegmentIndex(string First, string Last, long Count);
internal sealed record QueryCursor(string File, long Offset, string Filter);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredLog))]
[JsonSerializable(typeof(SegmentIndex))]
[JsonSerializable(typeof(QueryCursor))]
internal sealed partial class LogSpaceJsonContext : JsonSerializerContext;
