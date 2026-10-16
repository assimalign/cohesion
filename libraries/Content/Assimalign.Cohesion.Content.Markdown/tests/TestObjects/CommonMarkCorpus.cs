using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

// The only downloaded fixture is the official spec; see CommonMark/PROVENANCE.md.
// Degradation expectations are authored snapshots of this package's documented exclusions.
internal static class CommonMarkCorpus
{
    internal static readonly HashSet<string> NonRetainedSections = new(StringComparer.Ordinal)
    {
        "Setext headings", "Indented code blocks", "HTML blocks", "Link reference definitions", "Raw HTML",
    };

    internal static readonly string[] Sections =
    [
        "Tabs", "Backslash escapes", "Entity and numeric character references", "Precedence",
        "Thematic breaks", "ATX headings", "Setext headings", "Indented code blocks", "Fenced code blocks",
        "HTML blocks", "Link reference definitions", "Paragraphs", "Blank lines", "Block quotes",
        "List items", "Lists", "Inlines", "Code spans", "Emphasis and strong emphasis", "Links",
        "Images", "Autolinks", "Raw HTML", "Hard line breaks", "Soft line breaks", "Textual content",
    ];

    internal static string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, "TestObjects", "CommonMark", name);

    internal static IEnumerable<Example> ReadExamples()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(PathFor("spec-0.31.2.json")));
        foreach (var row in json.RootElement.EnumerateArray())
        {
            yield return new Example(row.GetProperty("example").GetInt32(), row.GetProperty("section").GetString()!,
                row.GetProperty("markdown").GetString()!, row.GetProperty("html").GetString()!);
        }
    }

    internal static Dictionary<int, Degradation> ReadDegradations()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(PathFor("degradations.json")));
        var result = new Dictionary<int, Degradation>();
        foreach (var row in json.RootElement.EnumerateArray())
        {
            result.Add(row.GetProperty("example").GetInt32(), new Degradation(
                row.GetProperty("section").GetString()!, row.GetProperty("reason").GetString()!,
                row.GetProperty("html").GetString()!, row.GetProperty("matchesSpec").GetBoolean()));
        }

        return result;
    }

    internal sealed record Example(int Number, string Section, string Markdown, string Html);
    internal sealed record Degradation(string Section, string Reason, string Html, bool MatchesSpec);
}
