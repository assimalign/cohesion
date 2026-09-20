using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Types.Tests;

/// <summary>Tests persisted comparison values and compatibility decoding.</summary>
public class CollationEncodingTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Types] - Encoding: folding collation keys retain canonical values and component boundaries")]
    [InlineData(2, "ÉCOLE\0ABC", "école\0abc")]
    [InlineData(3, "ÉCOLE\0ABC", "ecole\0abc")]
    public void ReadString_FoldedCompositeKey_ShouldDecodeCanonicalValue(byte id, string input, string expected)
    {
        // Arrange
        var writer = new DatabaseKeyWriter().AppendString(input, Collation.FromId(id)).AppendInt32(42);

        // Act / Assert
        var reader = new DatabaseKeyReader(writer.WrittenSpan);
        reader.ReadString(out Collation collation).ShouldBe(expected);
        collation.Id.ShouldBe(id);
        reader.ReadInt32().ShouldBe(42);
        reader.IsAtEnd.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: existing binary key bytes remain unchanged")]
    public void AppendString_Binary_ShouldPreserveExistingFormat()
    {
        byte[] actual = new DatabaseKeyWriter().AppendString("A\0é", Collation.Binary).ToArray();
        actual.ShouldBe(new byte[] { (byte)DatabaseType.String, 0, 0x41, 0, 0xFF, 0xC3, 0xA9, 0, 0 });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: historical invariant keys remain readable")]
    public void ReadString_LegacyInvariantKey_ShouldDecodeOriginalSpelling()
    {
        // Arrange: historical layout is tag, collation, escaped linguistic key, length, original UTF-8.
        byte[] legacy = [(byte)DatabaseType.String, 1, 1, 0, 0, 0, 0, 0, 3, 65, 98, 99];

        // Act / Assert
        var reader = new DatabaseKeyReader(legacy);
        reader.ReadString(out Collation collation).ShouldBe("Abc");
        collation.ShouldBeSameAs(Collation.Invariant);
        reader.IsAtEnd.ShouldBeTrue();
    }
}
