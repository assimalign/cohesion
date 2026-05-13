using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Ini.Tests;

using Assimalign.Cohesion.Configuration;

/// <summary>
/// Grammar-level coverage of <see cref="IniConfigurationParser"/>. Tests here exercise
/// the parser directly through reflection (the type is internal) so they cover the
/// supported INI contract in isolation from the provider lifecycle. Provider-level
/// scenarios live in <see cref="ConfigurationIniProviderTests"/>.
/// </summary>
public class IniConfigurationParserTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: root keys map to their bare key")]
    public async Task Parser_RootKeys_MapToBareKey()
    {
        IDictionary<Path, string?> entries = await ParseAsync("""
            Mode = Live
            Region = us-east-1
            """);

        Assert.Equal("Live", entries[Path("Mode")]);
        Assert.Equal("us-east-1", entries[Path("Region")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: section nests keys under section name")]
    public async Task Parser_Section_NestsKeysUnderSectionName()
    {
        IDictionary<Path, string?> entries = await ParseAsync("""
            [Logging]
            Level = Debug
            Enabled = true
            """);

        Assert.Equal("Debug", entries[Path("Logging", "Level")]);
        Assert.Equal("true", entries[Path("Logging", "Enabled")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: colon-separated section name nests deeply")]
    public async Task Parser_NestedSection_NestsKeysAtEachLevel()
    {
        // Documented behavior: '[Logging:Console]' + 'Level = Debug' resolves to
        // path Logging:Console:Level.
        IDictionary<Path, string?> entries = await ParseAsync("""
            [Logging:Console]
            Level = Debug
            """);

        Assert.Equal("Debug", entries[Path("Logging", "Console", "Level")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: comments and blank lines are ignored")]
    public async Task Parser_CommentsAndBlankLines_AreIgnored()
    {
        IDictionary<Path, string?> entries = await ParseAsync("""
            ; semicolon comment at column 0
                # hash comment after whitespace

            [Section]
            ; comment mid-section
            Key = Value
            """);

        Assert.Single(entries);
        Assert.Equal("Value", entries[Path("Section", "Key")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: whitespace around key, section, and delimiter is normalized")]
    public async Task Parser_Whitespace_IsNormalized()
    {
        IDictionary<Path, string?> entries = await ParseAsync("""
            [  Logging  :  Console  ]
                Level    =    Debug
            """);

        Assert.Equal("Debug", entries[Path("Logging", "Console", "Level")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: equals signs in value are preserved")]
    public async Task Parser_EqualsInsideValue_IsPreserved()
    {
        // Only the first '=' is the delimiter; the rest belong to the value.
        IDictionary<Path, string?> entries = await ParseAsync("ConnectionString = Server=db;User=app");

        Assert.Equal("Server=db;User=app", entries[Path("ConnectionString")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: matched surrounding quotes are stripped, inner text literal")]
    public async Task Parser_QuotedValue_IsTreatedAsLiteralText()
    {
        IDictionary<Path, string?> entries = await ParseAsync("""
            Greeting = "Hello, World"
            Single = 'a value'
            Mismatched = "no closing quote
            """);

        Assert.Equal("Hello, World", entries[Path("Greeting")]);
        Assert.Equal("a value", entries[Path("Single")]);
        // Mismatched quotes: only one side has a quote, so it's a literal value.
        Assert.Equal("\"no closing quote", entries[Path("Mismatched")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: duplicate keys resolve last-value-wins")]
    public async Task Parser_DuplicateKey_ResolvesLastValueWins()
    {
        IDictionary<Path, string?> entries = await ParseAsync("""
            [Cache]
            Mode = Memory
            Mode = Distributed
            """);

        Assert.Equal("Distributed", entries[Path("Cache", "Mode")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: malformed section header throws FormatException")]
    public async Task Parser_MalformedSectionHeader_Throws()
    {
        // Opens with '[' but never closes.
        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => ParseAsync("[Logging\nLevel = Debug"));

        Assert.Contains("section header", ex.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: empty section header throws FormatException")]
    public async Task Parser_EmptySectionHeader_Throws()
    {
        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => ParseAsync("[]\nKey = Value"));

        Assert.Contains("empty", ex.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: section with empty segment throws FormatException")]
    public async Task Parser_SectionWithEmptySegment_Throws()
    {
        // ':' delimiter with nothing on one side -> empty segment.
        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => ParseAsync("[Logging::Console]\nLevel = Debug"));

        Assert.Contains("empty segment", ex.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: assignment without '=' throws FormatException")]
    public async Task Parser_AssignmentWithoutDelimiter_Throws()
    {
        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => ParseAsync("OrphanLine"));

        Assert.Contains("'='", ex.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: assignment without key throws FormatException")]
    public async Task Parser_AssignmentWithoutKey_Throws()
    {
        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => ParseAsync("= ValueOnly"));

        Assert.Contains("missing a key", ex.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: utf-8 BOM is skipped")]
    public async Task Parser_Utf8BomPrefix_IsSkipped()
    {
        // Real-world Windows-authored INI files often ship with a UTF-8 BOM.
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] body = Encoding.UTF8.GetBytes("[Section]\nKey=Value");
        using var stream = new MemoryStream([.. bom, .. body]);

        var entries = new Dictionary<Path, string?>();
        await IniConfigurationParser_ParseAsync(stream, entries);

        Assert.Equal("Value", entries[Path("Section", "Key")]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: rejects null stream")]
    public async Task Parser_NullStream_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => IniConfigurationParser_ParseAsync(null!, new Dictionary<Path, string?>()));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Parser: rejects null entries")]
    public async Task Parser_NullEntries_Throws()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => IniConfigurationParser_ParseAsync(stream, null!));
    }

    // ---- helpers ----

    private static Path Path(params string[] segments)
    {
        var keys = new Key[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            keys[i] = new Key(segments[i]);
        }
        return new Path(keys);
    }

    private static async Task<IDictionary<Path, string?>> ParseAsync(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var entries = new Dictionary<Path, string?>();
        await IniConfigurationParser_ParseAsync(stream, entries);
        return entries;
    }

    // The parser is internal to the production assembly. Reach it via reflection so
    // these tests stay grammar-focused without needing an InternalsVisibleTo hook.
    // Trim warnings (IL2026/IL2075) are intentional: this is test-only reflection
    // and we never trim the test assembly.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Test-only reflection; test assemblies are not trimmed.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicAccessToType",
        Justification = "Test-only reflection; test assemblies are not trimmed.")]
    private static async Task IniConfigurationParser_ParseAsync(Stream stream, IDictionary<Path, string?> entries)
    {
        Assembly asm = typeof(ConfigurationIniProvider).Assembly;
        Type parser = asm.GetType("Assimalign.Cohesion.Configuration.Ini.IniConfigurationParser")
            ?? throw new InvalidOperationException("IniConfigurationParser type not found in production assembly.");
        MethodInfo method = parser.GetMethod("ParseAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("IniConfigurationParser.ParseAsync(Stream, IDictionary, CancellationToken) not found.");

        try
        {
            var task = (Task)method.Invoke(null, [stream, entries, CancellationToken.None])!;
            await task.ConfigureAwait(false);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
