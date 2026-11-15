using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Text.Tests;

public sealed class TextVectorTests
{
    public static IEnumerable<object[]> TerminalCorpus => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestObjects", "VectorPostgres17.tsv"))
        .Select(line => line.Split('\t')).Select(parts => new object[] { parts[0], parts[2], parts[3] });

    [Theory(DisplayName = "Cohesion Test [Text] - Vector: documented terminal weight departures are pinned against PostgreSQL")]
    [MemberData(nameof(TerminalCorpus))]
    public void Create_TerminalWeightDepartures_KeepsHighestWeight(string name, string postgres, string documented)
    {
        // Arrange
        TextVector vector;
        if (name == "count-cutoff")
        {
            TextLexeme first = TextLexeme.Create("x", Enumerable.Range(1, 255).Select(n => TextPosition.Create(n)));
            TextLexeme last = TextLexeme.Create("x", [TextPosition.Create(256, TextWeight.B), TextPosition.Create(256, TextWeight.A)]);
            vector = TextVector.Create([first, last]);
            postgres.ShouldBe("'x':" + string.Join(",", Enumerable.Range(1, 255)) + ",256B");
        }
        else
        {
            name.ShouldBe("position-cutoff");
            vector = TextVector.Create([TextLexeme.Create("x", [TextPosition.Create(16383, TextWeight.A),
                TextPosition.Create(16383, TextWeight.B), TextPosition.Create(1), TextPosition.Create(2),
                TextPosition.Create(3), TextPosition.Create(4), TextPosition.Create(5)])]);
            postgres.ShouldBe("'x':1,2,3,4,5,16383B");
        }

        // Act / Assert
        VectorText.Format(vector).ShouldBe(documented);
        documented.ShouldNotBe(postgres);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: terminal duplicate positions retain the highest weight")]
    public void Create_DuplicateAtClampedLimit_KeepsHighestWeightAtCutoff()
    {
        // Arrange
        // PostgreSQL 17.11: 'a:1A,2B,3C,4D,16382,16383,16384A,20000B'
        // renders 'a':1A,2B,3C,4,16382,16383. Text keeps the highest duplicate weight.
        TextPosition[] positions = [TextPosition.Create(1, TextWeight.A), TextPosition.Create(2, TextWeight.B),
            TextPosition.Create(3, TextWeight.C), TextPosition.Create(4), TextPosition.Create(16382),
            TextPosition.Create(16383), TextPosition.Create(16384, TextWeight.A), TextPosition.Create(20000, TextWeight.B)];

        // Act
        TextVector vector = TextVector.Create([TextLexeme.Create("a", positions)]);

        // Assert
        VectorText.Format(vector).ShouldBe("'a':1A,2B,3C,4,16382,16383A");
        VectorText.Format(TextVector.Create([TextLexeme.Create("a", [TextPosition.Create(16383), TextPosition.Create(16384, TextWeight.A)])]))
            .ShouldBe("'a':16383A");
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: structural equality includes lexemes, positions and weights")]
    public void Equals_CanonicalValues_ComparesContent()
    {
        // Arrange
        TextVector first = TextVector.Create([TextLexeme.Create("fox", [TextPosition.Create(2, TextWeight.A), TextPosition.Create(1)])]);
        TextVector same = TextVector.Create([TextLexeme.Create("fox", [TextPosition.Create(1), TextPosition.Create(2, TextWeight.A)])]);
        TextVector other = TextVector.Create([TextLexeme.Create("fox", [TextPosition.Create(1), TextPosition.Create(2)])]);

        // Act / Assert
        first.ShouldBe(same);
        first.GetHashCode().ShouldBe(same.GetHashCode());
        first.ShouldNotBe(other);
        first.Lexemes[0].ShouldBe(same.Lexemes[0]);
        first.Equals(null).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: merges and snapshots lexemes, positions and weights in UTF-8 order")]
    public void Create_DuplicateUnsortedLexemes_MergesAndSnapshots()
    {
        // Arrange
        var source = new[] { TextPosition.Create(9), TextPosition.Create(1, TextWeight.C), TextPosition.Create(9, TextWeight.A) };
        TextLexeme first = TextLexeme.Create("z", source);
        source[0] = TextPosition.Create(2);

        // Act
        TextVector vector = TextVector.Create([first, TextLexeme.Create("z", [TextPosition.Create(1, TextWeight.B)]),
            TextLexeme.Create("𐐨"), TextLexeme.Create("\ue000"), TextLexeme.Create("a")]);

        // Assert
        VectorText.Format(vector).ShouldBe("'a' 'z':1B,9A '\ue000' '𐐨'");
        Should.Throw<NotSupportedException>(() => ((IList<TextPosition>)first.Positions)[0] = TextPosition.Create(3));
        Should.Throw<NotSupportedException>(() => ((IList<TextLexeme>)vector.Lexemes)[0] = first);
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Position: PostgreSQL clamps values above the 14-bit maximum")]
    [InlineData(1, 1)]
    [InlineData(16382, 16382)]
    [InlineData(16383, 16383)]
    [InlineData(16384, 16383)]
    [InlineData(int.MaxValue, 16383)]
    public void Create_PositionLimit_Clamps(int input, int expected)
    {
        // Arrange / Act
        TextPosition position = TextPosition.Create(input, TextWeight.A);

        // Assert
        position.Position.ShouldBe(expected);
        position.Weight.ShouldBe(TextWeight.A);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: explicit construction retains 256 positions, analysis retains PostgreSQL's 255")]
    public void Create_PositionCountLimit_RetainsEarliestAndHighestWeight()
    {
        // Arrange
        TextPosition[] positions = Enumerable.Range(1, 257).Reverse().Select(n => TextPosition.Create(n)).ToArray();

        // Act
        TextVector explicitVector = TextVector.Create([TextLexeme.Create("a", positions)]);
        TextVector analyzed = TextAnalyzer.Simple.Analyze(string.Concat(Enumerable.Repeat("a ", 257)));

        // Assert
        explicitVector.Lexemes[0].Positions.Count.ShouldBe(256);
        explicitVector.Lexemes[0].Positions[255].Position.ShouldBe(256);
        analyzed.Lexemes[0].Positions.Count.ShouldBe(255);
        analyzed.Lexemes[0].Positions[254].Position.ShouldBe(255);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: lexemes permit 2047 bytes; the analyzer skips raw words at 2047 bytes without a position")]
    public void Analyze_LexemeByteEdges_MatchesPostgres17ConstructionPaths()
    {
        // Arrange
        string accepted = new('x', 2046), skipped = new('x', 2047);

        // Act
        TextVector vector = TextAnalyzer.Simple.Analyze(accepted + " " + skipped + " " + new string('x', 2048) + " z");

        // Assert
        vector.Lexemes.Count.ShouldBe(2);
        vector.Lexemes[1].Text.ShouldBe("z");
        vector.Lexemes[1].Positions[0].Position.ShouldBe(2);
        TextLexeme.Create(skipped).Text.Length.ShouldBe(2047);
        Should.Throw<TextException>(() => TextLexeme.Create(new string('x', 2048))).Code.ShouldBe(TextErrorCode.InvalidLexeme);
        TextAnalyzer.Simple.Analyze(new string('é', 1023)).Lexemes.Count.ShouldBe(1);
        TextAnalyzer.Simple.Analyze(new string('é', 1024) + " z").Lexemes[0].Positions[0].Position.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: case-fold expansion can reach the 2047-byte value limit, and overflow leaves a position gap")]
    public void Analyze_CaseFoldByteExpansion_MatchesPostgres17ValueLimitAndGaps()
    {
        // Arrange
        // PostgreSQL 17.11: octet_length((tsvector_to_array(to_tsvector('simple',
        // 'Ⱦ' || repeat('x',2044))))[1]) = 2047, also for english.
        // Two Ⱦ characters plus 2042 x's grow from 2046 to 2048 bytes; appending
        // ' z' produces 'z':2 after the oversize normalized lexeme is skipped.
        string maximum = "Ⱦ" + new string('x', 2044);
        string overflow = "ȾȾ" + new string('x', 2042) + " z";

        // Act / Assert
        foreach (TextAnalyzer analyzer in new[] { TextAnalyzer.Simple, TextAnalyzer.English })
        {
            TextVector vector = analyzer.Analyze(maximum);
            vector.Lexemes.Count.ShouldBe(1);
            System.Text.Encoding.UTF8.GetByteCount(vector.Lexemes[0].Text).ShouldBe(2047);
            vector.Lexemes[0].Positions[0].Position.ShouldBe(1);
            VectorText.Format(analyzer.Analyze(overflow)).ShouldBe("'z':2");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: the lexeme/position area has PostgreSQL's 1048575-byte bound")]
    public void Create_PayloadByteEdge_AcceptsMaximumAndRefusesNextByte()
    {
        // Arrange
        // 512 * 2046 + 1023 = 1048575 stripped bytes, independent of lexeme order.
        var lexemes = Enumerable.Range(0, 512).Select(n => TextLexeme.Create(n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            + new string('x', 2042))).ToList();
        lexemes.Add(TextLexeme.Create(new string('z', 1023)));

        // Act
        TextVector maximum = TextVector.Create(lexemes);
        lexemes.Add(TextLexeme.Create("q"));
        TextException failure = Should.Throw<TextException>(() => TextVector.Create(lexemes));

        // Assert
        maximum.PayloadBytes.ShouldBe(1048575);
        failure.Code.ShouldBe(TextErrorCode.VectorTooLarge);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: position alignment and count bytes participate in the payload bound")]
    public void Create_PositionPayload_CountsAlignmentAndRejectsOversizeAnalysis()
    {
        // Arrange
        TextLexeme lexeme = TextLexeme.Create("abc", [TextPosition.Create(1)]);
        string document = string.Join(" ", Enumerable.Range(0, 10000).Select(n => "term" + n.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 100)));

        // Act
        TextVector vector = TextVector.Create([lexeme]);
        TextException failure = Should.Throw<TextException>(() => TextAnalyzer.Simple.Analyze(document));

        // Assert
        vector.PayloadBytes.ShouldBe(8);
        failure.Code.ShouldBe(TextErrorCode.VectorTooLarge);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Vector: invalid scalars, empty lexemes and default positions are refused")]
    public void Create_InvalidValues_RefusesStructuredOrArgumentFailure()
    {
        // Arrange / Act / Assert
        Should.Throw<TextException>(() => TextLexeme.Create("")).Code.ShouldBe(TextErrorCode.InvalidLexeme);
        Should.Throw<TextException>(() => TextLexeme.Create("a\0b")).Code.ShouldBe(TextErrorCode.InvalidLexeme);
        Should.Throw<TextException>(() => TextLexeme.Create("\ud800")).Code.ShouldBe(TextErrorCode.InvalidUnicode);
        Should.Throw<TextException>(() => TextAnalyzer.Simple.Analyze("a\udfff")).Code.ShouldBe(TextErrorCode.InvalidUnicode);
        Should.Throw<ArgumentOutOfRangeException>(() => TextLexeme.Create("a", [default]));
        Should.Throw<ArgumentOutOfRangeException>(() => TextPosition.Create(0));
        Should.Throw<ArgumentOutOfRangeException>(() => TextPosition.Create(1, (TextWeight)4));
        Should.Throw<ArgumentOutOfRangeException>(() => TextAnalyzer.Simple.Analyze("a", (TextWeight)4));
        TextVector.Create([]).ShouldBeSameAs(TextVector.Empty);
    }
}
