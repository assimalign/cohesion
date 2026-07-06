using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.OpenApi.Compliance.Tests;

/// <summary>
/// Locates the vendored official example corpus copied next to the test assembly and exposes it to the
/// data-driven compliance theories.
/// </summary>
public static class CorpusFixtures
{
    private static string CorpusRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Corpus");

    /// <summary>Gets the JSON corpus files as xUnit theory data (relative path).</summary>
    /// <returns>One row per JSON corpus file.</returns>
    public static IEnumerable<object[]> JsonFiles() => Files("*.json");

    /// <summary>
    /// The corpus examples whose upstream JSON and YAML files are not the same document. The official
    /// <c>learn.openapis.org</c> corpus does not keep these two pairs in sync — the YAML forms carry
    /// advanced surfaces (webhooks, callbacks, links) the JSON forms omit — so they are excluded from the
    /// format-equivalence theory but still exercised by the parse, round-trip, and validation theories.
    /// </summary>
    private static readonly string[] DivergentPairs = ["tictactoe", "3.2-query-example"];

    /// <summary>Gets the base names paired with their JSON and YAML paths, for round-trip tests.</summary>
    /// <returns>One row per JSON/YAML corpus pair.</returns>
    public static IEnumerable<object[]> JsonYamlPairs() => Pairs(includeDivergent: true);

    /// <summary>Gets the JSON/YAML pairs whose two files are the same document, for the equivalence test.</summary>
    /// <returns>One row per equivalent JSON/YAML corpus pair.</returns>
    public static IEnumerable<object[]> EquivalentJsonYamlPairs() => Pairs(includeDivergent: false);

    private static IEnumerable<object[]> Pairs(bool includeDivergent)
    {
        foreach (var json in Enumerate("*.json"))
        {
            var yaml = Path.ChangeExtension(json, ".yaml");
            if (!File.Exists(yaml))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(json);
            if (!includeDivergent && Array.IndexOf(DivergentPairs, name) >= 0)
            {
                continue;
            }

            yield return [Relative(json), Relative(yaml)];
        }
    }

    /// <summary>Reads the text of a corpus file identified by its path relative to the corpus root.</summary>
    /// <param name="relative">The path relative to the <c>Corpus</c> directory.</param>
    /// <returns>The file contents.</returns>
    public static string ReadRelative(string relative) => File.ReadAllText(Path.Combine(CorpusRoot, relative));

    private static IEnumerable<object[]> Files(string pattern) => Enumerate(pattern).Select(path => new object[] { Relative(path) });

    private static IEnumerable<string> Enumerate(string pattern) =>
        Directory.Exists(CorpusRoot)
            ? Directory.EnumerateFiles(CorpusRoot, pattern, SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            : [];

    private static string Relative(string path) => Path.GetRelativePath(CorpusRoot, path);
}
