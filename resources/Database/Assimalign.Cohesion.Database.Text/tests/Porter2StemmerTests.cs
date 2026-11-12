using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Text.Internal;

namespace Assimalign.Cohesion.Database.Text.Tests;

public sealed class Porter2StemmerTests
{
    private readonly ITestOutputHelper _output;

    public Porter2StemmerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Porter2: matches the PostgreSQL 17 dictionary over documentation and suffix variants")]
    public void Stem_Postgres17Corpus_MatchesEveryPairAndExercisesEveryStep()
    {
        // Arrange
        string[] pairs = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "EnglishPostgres17.tsv"));
        var words = new HashSet<string>(StringComparer.Ordinal);
        var hits = new int[13];
        var mismatches = new List<string>();

        // Act
        foreach (string pair in pairs)
        {
            int tab = pair.IndexOf('\t');
            string word = pair[..tab], expected = pair[(tab + 1)..];
            words.Add(word).ShouldBeTrue();
            if (EnglishStopWords.Contains(word))
            {
                expected.ShouldBeEmpty(word);
                continue;
            }

            string actual = Porter2Stemmer.Stem(word, out Porter2Steps steps);
            for (int bit = 0; bit < hits.Length; bit++)
            {
                if (((int)steps & (1 << bit)) != 0)
                {
                    hits[bit]++;
                }
            }

            if (actual != expected && mismatches.Count < 40)
            {
                mismatches.Add($"{word}: expected {expected}, actual {actual}");
            }
        }

        // Assert
        words.Count.ShouldBeGreaterThanOrEqualTo(20000);
        for (int bit = 0; bit < hits.Length; bit++)
        {
            _output.WriteLine($"{(Porter2Steps)(1 << bit)}: {hits[bit]}");
            hits[bit].ShouldBeGreaterThan(0);
        }

        _output.WriteLine($"Vocabulary pairs: {words.Count}");
        mismatches.ShouldBeEmpty();
    }
}
