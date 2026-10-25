using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

[DisplayName("Origins policy")]
[Description("Which origins may call the resource.")]
internal sealed class TestPolicyOptions
{
    [DisplayName("Allowed origins")]
    [Description("Absolute origins allowed to call the resource.")]
    public string[] Origins { get; init; } = [];

    public int? MaxAgeSeconds { get; init; }

    public bool AllowCredentials { get; init; }
}

internal sealed class TestLimitsOptions
{
    public int MaxConnections { get; init; } = 100;
}

internal sealed class TestDatabaseEntry
{
    public string Engine { get; init; } = string.Empty;
}

internal sealed class TestFlushInput
{
    public string? Prefix { get; init; }
}

internal sealed class TestFlushResult
{
    public int Flushed { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TestPolicyOptions))]
[JsonSerializable(typeof(TestLimitsOptions))]
[JsonSerializable(typeof(Dictionary<string, TestDatabaseEntry>))]
[JsonSerializable(typeof(TestFlushInput))]
[JsonSerializable(typeof(TestFlushResult))]
[JsonSerializable(typeof(string))]
internal sealed partial class TestSurfaceJsonContext : JsonSerializerContext;
