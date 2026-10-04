using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// The row codec's column splice, DROP COLUMN's row rewrite (#1237): it removes one
/// stored component and copies every other byte, so a rewritten version is always
/// shorter than its slot and never has to move.
/// </summary>
public sealed class SqlRowCodecTests
{
    private const ulong ObjectId = 42;

    private static readonly SqlCatalogColumn[] Columns =
    [
        new("id", new DatabaseTypeInfo(DatabaseType.Int32)),
        new("note", new DatabaseTypeInfo(DatabaseType.String)),
        new("amount", new DatabaseTypeInfo(DatabaseType.Decimal)),
        new("token", new DatabaseTypeInfo(DatabaseType.Guid)),
    ];

    private static readonly object?[] Values = [7, "dropped text", 12.50m, Guid.Parse("5b8f8a59-7a2c-4b45-9d55-0a4d3a5f0c11")];

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Row codec: WithoutColumn removes exactly the stored component and keeps every other byte and both stamps")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WithoutColumn_StoredComponent_ShouldRemoveExactlyItsBytes(int ordinal)
    {
        // Arrange: a tombstoned version, so both stamps must survive.
        byte[] record = SqlRowCodec.WithDeleter(
            SqlRowCodec.Encode(ObjectId, Columns, Values, new TransactionSequence(5)), new TransactionSequence(9));
        var remaining = Columns.Where((_, index) => index != ordinal).ToArray();
        var remainingValues = Values.Where((_, index) => index != ordinal).ToArray();

        // Act
        byte[]? spliced = SqlRowCodec.WithoutColumn(record, ObjectId, ordinal);

        // Assert: byte-identical to the same version written under the new layout, shorter by
        // exactly the removed component, and decodable on that layout.
        spliced.ShouldNotBeNull();
        spliced.ShouldBe(SqlRowCodec.WithDeleter(
            SqlRowCodec.Encode(ObjectId, remaining, remainingValues, new TransactionSequence(5)), new TransactionSequence(9)));
        var component = new DatabaseKeyWriter();
        SqlRowCodec.AppendValue(component, Columns[ordinal].Type.Type, Values[ordinal]);
        spliced.Length.ShouldBe(record.Length - component.ToArray().Length);

        var decoded = SqlRowCodec.TryDecode(spliced, ObjectId, remaining.Length, out var writer, out var deleter, out int stored);
        decoded.ShouldBe(remainingValues);
        writer.ShouldBe(new TransactionSequence(5));
        deleter.ShouldBe(new TransactionSequence(9));
        stored.ShouldBe(remaining.Length);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: WithoutColumn removes a stored NULL, the shortest component")]
    public void WithoutColumn_StoredNull_ShouldRemoveOneByte()
    {
        // Arrange
        object?[] values = [7, null, 12.50m, Values[3]];
        byte[] record = SqlRowCodec.Encode(ObjectId, Columns, values, new TransactionSequence(3));

        // Act
        byte[]? spliced = SqlRowCodec.WithoutColumn(record, ObjectId, 1);

        // Assert
        spliced.ShouldNotBeNull();
        spliced.Length.ShouldBe(record.Length - 1);
        SqlRowCodec.TryDecode(spliced, ObjectId, 3, out _, out _, out _).ShouldBe(new object?[] { 7, 12.50m, Values[3] });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a version that stores no component at the ordinal needs no rewrite")]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void WithoutColumn_OrdinalInMissingTail_ShouldReturnNull(int ordinal)
    {
        // Arrange: written before the last two columns were added.
        byte[] record = SqlRowCodec.Encode(ObjectId, Columns[..2], Values[..2], new TransactionSequence(3));

        // Act / Assert
        SqlRowCodec.WithoutColumn(record, ObjectId, ordinal).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a record of another object or without a stamp header needs no rewrite")]
    public void WithoutColumn_ForeignOrShortRecord_ShouldReturnNull()
    {
        // Arrange
        byte[] record = SqlRowCodec.Encode(ObjectId, Columns, Values, new TransactionSequence(3));

        // Act / Assert
        SqlRowCodec.WithoutColumn(record, ObjectId + 1, 1).ShouldBeNull();
        SqlRowCodec.WithoutColumn(record.AsSpan(0, SqlRowCodec.StampHeaderSize - 1), ObjectId, 1).ShouldBeNull();
        Should.Throw<ArgumentOutOfRangeException>(() => SqlRowCodec.WithoutColumn(record, ObjectId, -1));
    }
}
