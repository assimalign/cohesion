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

namespace Assimalign.Cohesion.Database.Types.Text.Tests;

public sealed class TextAnalyzerTests
{
    private readonly ITestOutputHelper _output;

    public TextAnalyzerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> ParserCorpus => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "ParserPostgres17.tsv"))
        .Select(line => line.Split('\t')).Select(parts => new object[] { parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], parts[7] });

    [Theory(DisplayName = "Cohesion Test [Text] - Analyzer: PostgreSQL parser agreements and deliberate letter/digit departures are pinned")]
    [MemberData(nameof(ParserCorpus))]
    public void Analyze_ParserCorpus_MatchesPinnedOutput(string category, string input, string postgresSimple, string postgresEnglish, string simple, string english, string simpleContract, string englishContract)
    {
        // Arrange / Act
        string actualSimple = VectorText.Format(TextAnalyzer.Simple.Analyze(input));
        string actualEnglish = VectorText.Format(TextAnalyzer.English.Analyze(input));

        // Assert
        VerifyContract(actualSimple, postgresSimple, simple, simpleContract, category);
        VerifyContract(actualEnglish, postgresEnglish, english, englishContract, category);
    }

    private static void VerifyContract(string actual, string postgres, string documented, string contract, string category)
    {
        if (contract == "agreement")
        {
            actual.ShouldBe(postgres, category);
            documented.ShouldBe(postgres, category);
        }
        else
        {
            contract.ShouldBe("departure");
            actual.ShouldBe(documented, category + " PostgreSQL: " + postgres);
            documented.ShouldNotBe(postgres, category);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Descriptor: unknown persisted identities are refused without fallback")]
    [InlineData("unknown", 1, TextErrorCode.UnknownAnalyzer)]
    [InlineData("Simple", 1, TextErrorCode.UnknownAnalyzer)]
    [InlineData("", 1, TextErrorCode.UnknownAnalyzer)]
    [InlineData("english", 0, TextErrorCode.AnalyzerVersionMismatch)]
    [InlineData("english", -1, TextErrorCode.AnalyzerVersionMismatch)]
    [InlineData("simple", int.MinValue, TextErrorCode.AnalyzerVersionMismatch)]
    [InlineData("simple", 2, TextErrorCode.AnalyzerVersionMismatch)]
    [InlineData("english", 2, TextErrorCode.AnalyzerVersionMismatch)]
    public void Create_UnavailableIdentity_RefusesWithCode(string name, int version, TextErrorCode expected)
    {
        // Arrange
        TextAnalyzerDescriptor descriptor = TextAnalyzerDescriptor.Create(name, version);

        // Act
        TextException error = Should.Throw<TextException>(() => TextAnalyzer.Create(descriptor));

        // Assert
        error.Code.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Descriptor: all persisted options participate in identity")]
    public void Create_IdentityFields_RoundTripsAndDistinguishesOptions()
    {
        // Arrange
        TextAnalyzerDescriptor simple = TextAnalyzerDescriptor.Create("simple", 1);

        // Act / Assert
        simple.ShouldBe(TextAnalyzerDescriptor.Simple);
        simple.GetHashCode().ShouldBe(TextAnalyzerDescriptor.Simple.GetHashCode());
        simple.ShouldNotBe(TextAnalyzerDescriptor.Create("simple", 1, true));
        simple.ShouldNotBe(TextAnalyzerDescriptor.English);
        TextAnalyzer.Create(simple).Descriptor.ShouldBe(simple);
        TextAnalyzerDescriptor.Create("simple").ShouldBe(simple);
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Descriptor: creation follows each analyzer's current version")]
    [InlineData("simple", false)]
    [InlineData("simple", true)]
    [InlineData("english", false)]
    [InlineData("english", true)]
    public void Create_CurrentAnalyzerVersion_FollowsBuiltInIdentity(string name, bool foldAccents)
    {
        // Arrange
        TextAnalyzerDescriptor current = name == "simple" ? TextAnalyzerDescriptor.Simple : TextAnalyzerDescriptor.English;

        // Act
        TextAnalyzerDescriptor descriptor = TextAnalyzerDescriptor.Create(name, foldAccents);

        // Assert
        descriptor.Name.ShouldBe(current.Name);
        descriptor.Version.ShouldBe(current.Version);
        descriptor.FoldAccents.ShouldBe(foldAccents);
        TextAnalyzer.Create(descriptor).Descriptor.ShouldBe(descriptor);
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
            const string text = "CAFÉ naïve İSTANBUL Σ ς ẞ 𐐀 cafe\u0301 ١٢٣ １２３ TITLE Index";
            TextAnalyzer folded = TextAnalyzer.Create(TextAnalyzerDescriptor.Create("simple", 1, true));
            TextAnalyzer english = TextAnalyzer.Create(TextAnalyzerDescriptor.Create("english", 1, true));

            // Act / Assert
            Analyze_AsciiAlphabet_MatchesTypesFolding();
            VectorText.Format(TextAnalyzer.Simple.Analyze(text)).ShouldBe("'café':8 'café':1 'index':12 'naïve':2 'title':11 'ß':6 'İstanbul':3 'σ':4,5 '١٢٣':9 '１２３':10 '𐐨':7");
            VectorText.Format(folded.Analyze(text)).ShouldBe("'cafe':1,8 'index':12 'istanbul':3 'naive':2 'title':11 'ß':6 'σ':4,5 '١٢٣':9 '１２３':10 '𐐨':7");
            VectorText.Format(english.Analyze(text)).ShouldBe("'cafe':1,8 'index':12 'istanbul':3 'naiv':2 'titl':11 'ß':6 'σ':4,5 '١٢٣':9 '１２３':10 '𐐨':7");
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
        TextAnalyzer accents = TextAnalyzer.Create(TextAnalyzerDescriptor.Create("simple", 1, true));

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
    [InlineData("simple", false, 1, "8E0247D1FB80D6A41C16D101E9635B28F271A8D86AC04C16E7960F2A8F0B55EE")]
    [InlineData("simple", true, 1, "6675A595CA9070755EFA038E377AA403CFDE3D86C68AAADF4C10D8D7DCA89032")]
    [InlineData("english", false, 1, "54F24E9CDC99B0EA4C9CD90D6AB739C8B50F18A52406B5DC23A229712C7CDE56")]
    [InlineData("english", true, 1, "784A63ADA9E4E920DF74F682B62C75410FCF2F98279693D8344D33CAE5A44D7E")]
    public void Analyze_VersionedFingerprint_RequiresVersionForOutputDrift(string name, bool foldAccents, int version, string expected)
    {
        // Arrange
        TextAnalyzer analyzer = TextAnalyzer.Create(TextAnalyzerDescriptor.Create(name, version, foldAccents));
        IEnumerable<string> corpus = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "ParserPostgres17.tsv")).Select(line => line.Split('\t')[1])
            .Concat(File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "EnglishPostgres17.tsv")).Select(line => line.Split('\t')[0]))
            .Concat(File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "EnglishStopWords127.tsv")));
        var output = new StringBuilder();

        // Act
        foreach (string text in corpus)
        {
            output.Append(VectorText.Format(analyzer.Analyze(text))).Append('\n');
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output.ToString())));

        // Assert
        _output.WriteLine($"{name} v{version} accents={foldAccents}: {hash}");
        hash.ShouldBe(expected);
    }
}
