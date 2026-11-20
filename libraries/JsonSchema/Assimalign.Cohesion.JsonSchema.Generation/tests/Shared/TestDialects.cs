using System;

using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

/// <summary>
/// The three dialects the generator writes, by the short names test data uses.
/// </summary>
internal static class TestDialects
{
    /// <summary>Gets the short names of the supported dialects, as theory data.</summary>
    public static TheoryData<string> Names => new() { "draft-07", "2019-09", "2020-12" };

    /// <summary>Returns the dialect a short name stands for.</summary>
    public static JsonSchemaDialect Get(string name) => name switch
    {
        "draft-07" => JsonSchemaDialect.Draft07,
        "2019-09" => JsonSchemaDialect.Draft201909,
        "2020-12" => JsonSchemaDialect.Draft202012,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a dialect the generator writes."),
    };

    /// <summary>Returns the keyword a dialect keeps its definitions under.</summary>
    public static string GetDefinitionsKeyword(string name) => name == "draft-07" ? "definitions" : "$defs";
}
