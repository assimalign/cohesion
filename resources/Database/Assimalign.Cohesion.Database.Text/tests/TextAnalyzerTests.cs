using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Text.Tests;

public sealed class TextAnalyzerTests
{
    private readonly ITestOutputHelper _output;

    public TextAnalyzerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> ParserCorpus => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "ParserPostgres17.tsv"))
        .Select(line => line.Split('\t')).Select(parts => new object[] { parts[0], parts[1], parts[2], parts[3], parts[4], parts[5] });

    [Theory(DisplayName = "Cohesion Test [Text] - Analyzer: PostgreSQL parser agreements and deliberate letter/digit departures are pinned")]
    [MemberData(nameof(ParserCorpus))]
    public void Analyze_ParserCorpus_MatchesPinnedOutput(string category, string input, string postgresSimple, string postgresEnglish, string simple, string english)
    {
        // Arrange / Act
        string actualSimple = VectorText.Format(TextAnalyzer.Simple.Analyze(input));
        string actualEnglish = VectorText.Format(TextAnalyzer.English.Analyze(input));

        // Assert
        actualSimple.ShouldBe(simple, category + " PostgreSQL: " + postgresSimple);
        actualEnglish.ShouldBe(english, category + " PostgreSQL: " + postgresEnglish);
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Descriptor: unknown persisted identities are refused without fallback")]
    [InlineData("unknown", 1, TextErrorReason.UnknownAnalyzer)]
    [InlineData("Simple", 1, TextErrorReason.UnknownAnalyzer)]
    [InlineData("", 1, TextErrorReason.UnknownAnalyzer)]
    [InlineData("simple", 2, TextErrorReason.AnalyzerVersionMismatch)]
    [InlineData("english", 2, TextErrorReason.AnalyzerVersionMismatch)]
    public void Create_UnavailableIdentity_RefusesWithReason(string name, int version, TextErrorReason expected)
    {
        // Arrange
        TextAnalyzerDescriptor descriptor = TextAnalyzerDescriptor.FromPersisted(name, version);

        // Act
        TextException error = Should.Throw<TextException>(() => TextAnalyzer.Create(descriptor));

        // Assert
        error.Reason.ShouldBe(expected);
        descriptor.Name.ShouldBe(name);
        descriptor.Version.ShouldBe(version);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Descriptor: all persisted options participate in identity")]
    public void FromPersisted_IdentityFields_RoundTripsAndDistinguishesOptions()
    {
        // Arrange
        TextAnalyzerDescriptor simple = TextAnalyzerDescriptor.FromPersisted("simple", 1);

        // Act / Assert
        simple.ShouldBe(TextAnalyzerDescriptor.Simple);
        simple.GetHashCode().ShouldBe(TextAnalyzerDescriptor.Simple.GetHashCode());
        simple.ShouldNotBe(TextAnalyzerDescriptor.FromPersisted("simple", 1, true));
        simple.ShouldNotBe(TextAnalyzerDescriptor.English);
        TextAnalyzer.Create(simple).Descriptor.ShouldBe(simple);
        Should.Throw<ArgumentOutOfRangeException>(() => TextAnalyzerDescriptor.FromPersisted("simple", 0));
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Analyzer: Types Unicode tables control folding independent of culture")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("el-GR")]
    public void Analyze_CultureChanges_ProducesPinnedUnicodeAndAccentOptions(string culture)
    {
        // Arrange
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        CultureInfo previousUi = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            const string text = "CAFÉ naïve İSTANBUL Σ ς ẞ 𐐀 cafe\u0301 ١٢٣ １２３";
            TextAnalyzer folded = TextAnalyzer.Create(TextAnalyzerDescriptor.FromPersisted("simple", 1, true));
            TextAnalyzer english = TextAnalyzer.Create(TextAnalyzerDescriptor.FromPersisted("english", 1, true));

            // Act / Assert
            VectorText.Format(TextAnalyzer.Simple.Analyze(text)).ShouldBe("'cafe':8 'café':1 'naïve':2 'ß':6 'İstanbul':3 'σ':4,5 '١٢٣':9 '１２３':10 '𐐨':7");
            VectorText.Format(folded.Analyze(text)).ShouldBe("'cafe':1,8 'istanbul':3 'naive':2 'ß':6 'σ':4,5 '١٢٣':9 '１２３':10 '𐐨':7");
            VectorText.Format(english.Analyze(text)).ShouldBe("'cafe':1,8 'istanbul':3 'naiv':2 'ß':6 'σ':4,5 '١٢٣':9 '１２３':10 '𐐨':7");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
            Thread.CurrentThread.CurrentUICulture = previousUi;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Analyzer: stop-word gaps, clamping and default D weights match PostgreSQL 17")]
    public void Analyze_StopWordsAndLargePositions_PreservesGapsAndClamps()
    {
        // Arrange
        string text = string.Concat(Enumerable.Repeat("the ", 16383)) + "fox fox";

        // Act / Assert
        VectorText.Format(TextAnalyzer.English.Analyze("the and a running foxes jumped")).ShouldBe("'fox':5 'jump':6 'run':4");
        VectorText.Format(TextAnalyzer.English.Analyze(text)).ShouldBe("'fox':16383");
        VectorText.Format(TextAnalyzer.Simple.Analyze("FOX fox", TextWeight.A)).ShouldBe("'fox':1A,2A");
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Analyzer: the ASCII fast path agrees with the Types tables for every ASCII letter and digit")]
    public void Analyze_AsciiAlphabet_MatchesTypesFolding()
    {
        // Arrange
        TextAnalyzer accents = TextAnalyzer.Create(TextAnalyzerDescriptor.FromPersisted("simple", 1, true));

        // Act / Assert
        for (int scalar = 0; scalar < 128; scalar++)
        {
            string token = ((char)scalar).ToString();
            if (char.IsAsciiLetterOrDigit((char)scalar))
            {
                TextAnalyzer.Simple.Analyze(token).Lexemes[0].Text.ShouldBe(Collation.CaseInsensitive.Normalize(token));
                accents.Analyze(token).Lexemes[0].Text.ShouldBe(Collation.CaseAccentInsensitive.Normalize(token));
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Analyzer: output hashes are pinned beside each analyzer version")]
    [InlineData("simple", false, 1, "7F1176222B2431EDBCB234D9BAD62BF8CF16A448AAC18E7DE9B7C02DF382CBF9")]
    [InlineData("simple", true, 1, "B8D667ECEA89C90C5081A585FEC37EA5A6C91FCC031DAD9DA3BF71C039573542")]
    [InlineData("english", false, 1, "C85950AD855469EEE4B01E8E5A51619BF8C6473BEE8E2EC40B8F78ACE20771BE")]
    [InlineData("english", true, 1, "301ED0FB1A3716424B3A9C8DBAD3287486CD71EA81F2AE1B65611A936AD206B5")]
    public void Analyze_VersionedFingerprint_RequiresVersionForOutputDrift(string name, bool foldAccents, int version, string expected)
    {
        // Arrange
        TextAnalyzer analyzer = TextAnalyzer.Create(TextAnalyzerDescriptor.FromPersisted(name, version, foldAccents));
        IEnumerable<string> corpus = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "ParserPostgres17.tsv")).Select(line => line.Split('\t')[1])
            .Concat(File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "EnglishPostgres17.tsv")).Select(line => line.Split('\t')[0]));
        var output = new StringBuilder();

        // Act
        foreach (string text in corpus)
        {
            output.Append(VectorText.Format(analyzer.Analyze(text))).Append('\n');
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output.ToString())));

        // Assert
        analyzer.Descriptor.Version.ShouldBe(version);
        _output.WriteLine($"{name} v{version} accents={foldAccents}: {hash}");
        hash.ShouldBe(expected);
    }
}
