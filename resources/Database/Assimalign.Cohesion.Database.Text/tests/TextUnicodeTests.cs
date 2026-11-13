using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Text.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Text.Tests;

public sealed class TextUnicodeTests
{
    private readonly ITestOutputHelper _output;

    public TextUnicodeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Stop words: exact set equals REL_17_11 english.stop")]
    public void Contains_PostgresStopWords_MatchesExactSet()
    {
        // Arrange
        string[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "EnglishStopWords127.tsv"));

        // Act / Assert
        expected.Length.ShouldBe(127);
        EnglishStopWords.Words.Order(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Folding: every included scalar has a versioned normalization fingerprint")]
    [InlineData(false, 1, "91C1D1E7FBCFB85B4A0359784B5FCDB5DF8ACB69C6D081206AF730F3E26A0095")]
    [InlineData(true, 1, "D9902F64D2C576C055F9408BD7152F7C9CBDE07B291910DF4E267EAD3DE60252")]
    public void Normalize_IncludedScalars_PreservesVersionedFingerprint(bool foldAccents, int version, string expected)
    {
        // Arrange
        Collation collation = foldAccents ? Collation.CaseAccentInsensitive : Collation.CaseInsensitive;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> scalarBytes = stackalloc byte[4];

        // Act
        for (int scalar = 0; scalar <= 0x10ffff; scalar++)
        {
            if (TextUnicodeData.IsLetterOrDigit(scalar) || TextUnicodeData.IsContinuation(scalar))
            {
                BinaryPrimitives.WriteInt32LittleEndian(scalarBytes, scalar);
                hash.AppendData(scalarBytes);
                hash.AppendData(Encoding.UTF8.GetBytes(collation.Normalize(char.ConvertFromUtf32(scalar))));
                hash.AppendData(new byte[] { 0 });
            }
        }

        string actual = Convert.ToHexString(hash.GetHashAndReset());

        // Assert
        _output.WriteLine($"Folding v{version} accents={foldAccents}: {actual}");
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Marks: canonical spellings agree when folding accents")]
    [InlineData("simple")]
    [InlineData("english")]
    public void Analyze_CanonicalSpellingsWithAccentsFolded_PreservesLexemesAndPositions(string name)
    {
        // Arrange
        TextAnalyzer analyzer = TextAnalyzer.Create(TextAnalyzerDescriptor.Create(name, foldAccents: true));

        // Act / Assert
        VectorText.Format(analyzer.Analyze("naïve café Ångström"))
            .ShouldBe(VectorText.Format(analyzer.Analyze("nai\u0308ve cafe\u0301 A\u030Angstro\u0308m")));
        VectorText.Format(analyzer.Analyze("\u0308\u200dalpha beta"))
            .ShouldBe("'alpha':1 'beta':2");
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Folding: Hangul decomposes before the folded byte limit")]
    public void Analyze_HangulAccentFolding_AppliesFoldedByteLimit()
    {
        // Arrange
        TextAnalyzer analyzer = TextAnalyzer.Create(TextAnalyzerDescriptor.Create("simple", foldAccents: true));

        // Act / Assert
        analyzer.Analyze("한국어").Lexemes[0].Text.ShouldBe("한국어");
        Encoding.UTF8.GetByteCount(analyzer.Analyze("한국어").Lexemes[0].Text).ShouldBe(24);
        VectorText.Format(analyzer.Analyze(new string('한', 228) + " z")).ShouldBe("'z':2");
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Stemmer: empty stem keeps the input as PostgreSQL does")]
    public void Stem_EmptyResult_ReturnsInput()
    {
        // Arrange / Act / Assert
        Porter2Stemmer.Stem("''s").ShouldBe("''s");
    }
}
