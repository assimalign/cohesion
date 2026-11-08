using System;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlIndexKeyEncodingTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Rollback key bounds: scalar extrema and generated values never exceed the encoder-derived bound (#1779)")]
    public void MaximumKeyLength_ScalarFamilies_ShouldBoundEncodedExtrema()
    {
        // Arrange: the existing seeded oracle includes numeric extrema, IEEE special
        // values, temporal endpoints/offsets, UUIDs, NULL and variable families.
        foreach (var (type, values, collation, size) in
            from item in Values()
            from collation in new[] { Collation.Binary, Collation.CaseInsensitive, Collation.CaseAccentInsensitive }
            from size in new int?[] { null, 1, 2, 128, 255, 256, int.MaxValue }
            select (item.Type, item.Values, collation, size))
        {
            // Act
            bool bounded = SqlRowCodec.TryGetMaximumKeyLength(new DatabaseTypeInfo(type, size), collation, out long bound);

            // Assert: unbounded and non-orderable storage families never acquire a proof.
            if (type is DatabaseType.Binary or DatabaseType.Json or DatabaseType.JsonBinary ||
                type is DatabaseType.String or DatabaseType.Character && size is null)
            { bounded.ShouldBeFalse(); continue; }
            bounded.ShouldBeTrue();
            foreach (object? value in values)
            {
                string? text = value is SqlCharacter character ? character.Text : value as string;
                if (text is not null && text.EnumerateRunes().Count() > size) { continue; }
                foreach (var order in Enum.GetValues<SqlCatalogIndexKeyOrder>())
                { ((long)Encode(type, value, collation, order).Length).ShouldBeLessThanOrEqualTo(bound, type + "/" + collation.Name); }
            }
        }
        SqlRowCodec.TryGetMaximumKeyLength(new DatabaseTypeInfo(DatabaseType.String, 1), Collation.Invariant, out _).ShouldBeFalse();
        SqlRowCodec.TryGetMaximumKeyLength(new DatabaseTypeInfo(DatabaseType.Null), Collation.Binary, out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback key bounds: every Unicode scalar and longest declared strings fit under all key orders (#1779)")]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void MaximumKeyLength_AllUnicodeScalars_ShouldBoundCollationExpansion(int collationId)
    {
        // Arrange: exhaust the pinned collation codec, rather than assuming UTF-8
        // width also bounds decomposition, case folding and escaped zero bytes.
        var collation = Collation.FromId((byte)collationId);
        var writer = new DatabaseKeyWriter();
        string longest = string.Empty;
        int maximum = 0;
        for (int scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (!Rune.IsValid(scalar)) { continue; }
            string text = new Rune(scalar).ToString();
            writer.Reset();
            SqlRowCodec.AppendKeyValue(writer, DatabaseType.String, text, collation);
            if (writer.Length > maximum) { maximum = writer.Length; longest = text; }
        }

        // Act / Assert: repeat the actual maximum-width scalar at each boundary.
        foreach (var (type, size) in
            from type in new[] { DatabaseType.String, DatabaseType.Character }
            from size in new[] { 1, 2, 85, 86, 113, 114, 128, 254, 255, 256, 1024 }
            select (type, size))
        {
            SqlRowCodec.TryGetMaximumKeyLength(new DatabaseTypeInfo(type, size), collation, out long bound).ShouldBeTrue();
            string text = string.Concat(Enumerable.Repeat(longest, size));
            object value = type == DatabaseType.Character ? new SqlCharacter(text) : text;
            foreach (var order in Enum.GetValues<SqlCatalogIndexKeyOrder>())
            {
                long encoded = Encode(type, value, collation, order).Length;
                encoded.ShouldBeLessThanOrEqualTo(bound);
                encoded.ShouldBe(bound); // Pins the tight boundary, including every overhead byte.
                ((long)Encode(type, null, collation, order).Length).ShouldBeLessThanOrEqualTo(bound);
            }
        }
    }
}
