using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Assimalign.Cohesion.SourceGeneration.Tests;

/// <summary>
/// The analyzer configuration MSBuild generates: per-file metadata for additional files, keyed by
/// the file's path.
/// </summary>
internal sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly Dictionary<string, TestOptions> _files = new(StringComparer.Ordinal);

    public override AnalyzerConfigOptions GlobalOptions { get; } = new TestOptions(new Dictionary<string, string>());

    /// <summary>Adds an additional file's metadata, as <c>build_metadata.AdditionalFiles.&lt;name&gt;</c> keys.</summary>
    public TestAnalyzerConfigOptionsProvider Add(string path, IReadOnlyDictionary<string, string> metadata)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in metadata)
        {
            values["build_metadata.AdditionalFiles." + pair.Key] = pair.Value;
        }

        _files[path] = new TestOptions(values);
        return this;
    }

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        _files.TryGetValue(textFile.Path, out TestOptions? options) ? options : GlobalOptions;

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public TestOptions(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);
    }
}
