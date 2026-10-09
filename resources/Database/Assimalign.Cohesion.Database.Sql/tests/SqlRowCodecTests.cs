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
/// The row codec's physical layout (#1241): a version stores one component per physical
/// column up to its last live one, a dropped column keeps its physical ordinal, and every definition of a table
/// decodes every version of it onto the right columns. The definitions below are the
/// life of one table: created, a middle column dropped, the name re-added (a new physical
/// ordinal at the end), then the last physical column dropped.
/// </summary>
public sealed class SqlRowCodecTests
{
    private const ulong ObjectId = 42;

    private static readonly SqlCatalogColumn Id = new("id", new DatabaseTypeInfo(DatabaseType.Int32));
    private static readonly SqlCatalogColumn Note = new("note", new DatabaseTypeInfo(DatabaseType.String));
    private static readonly SqlCatalogColumn Amount = new("amount", new DatabaseTypeInfo(DatabaseType.Decimal));
    private static readonly SqlCatalogColumn Token = new("token", new DatabaseTypeInfo(DatabaseType.Guid));
    private static readonly SqlCatalogColumn NoteAgain = new("note", new DatabaseTypeInfo(DatabaseType.Int64));

    private static readonly Guid TokenValue = Guid.Parse("5b8f8a59-7a2c-4b45-9d55-0a4d3a5f0c11");

    /// <summary>(id, note, amount, token): physical ordinals 0-3.</summary>
    private static readonly SqlCatalogTable Created = new(ObjectId, "dbo", "t", [Id, Note, Amount, Token]);

    /// <summary>note dropped: (id, amount, token) at physical 0, 2, 3.</summary>
    private static readonly SqlCatalogTable NoteDropped = new(ObjectId, "dbo", "t", [Id, Amount, Token], droppedColumnOrdinals: [1]);

    /// <summary>note re-added as a BIGINT: (id, amount, token, note) at physical 0, 2, 3, 4.</summary>
    private static readonly SqlCatalogTable NoteReadded = new(ObjectId, "dbo", "t", [Id, Amount, Token, NoteAgain], droppedColumnOrdinals: [1]);

    /// <summary>the re-added note dropped too: (id, amount, token) at physical 0, 2, 3.</summary>
    private static readonly SqlCatalogTable BothDropped = new(ObjectId, "dbo", "t", [Id, Amount, Token], droppedColumnOrdinals: [4, 1]);

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a dropped column keeps its physical ordinal and a re-added name takes a new one")]
    public void Layout_DropAndReAdd_ShouldKeepAndAppendPhysicalOrdinals()
    {
        Created.PhysicalColumnCount.ShouldBe(4);
        Enumerable.Range(0, 4).Select(Created.GetPhysicalOrdinal).ShouldBe([0, 1, 2, 3]);

        NoteDropped.PhysicalColumnCount.ShouldBe(4);
        NoteDropped.DroppedColumnOrdinals.ShouldBe([1]);
        Enumerable.Range(0, 3).Select(NoteDropped.GetPhysicalOrdinal).ShouldBe([0, 2, 3]);

        NoteReadded.PhysicalColumnCount.ShouldBe(5);
        Enumerable.Range(0, 4).Select(NoteReadded.GetPhysicalOrdinal).ShouldBe([0, 2, 3, 4]);

        BothDropped.PhysicalColumnCount.ShouldBe(5);
        BothDropped.DroppedColumnOrdinals.ShouldBe([1, 4]);
        Enumerable.Range(0, 3).Select(BothDropped.GetPhysicalOrdinal).ShouldBe([0, 2, 3]);
        Should.Throw<ArgumentOutOfRangeException>(() => BothDropped.GetPhysicalOrdinal(3));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a dropped ordinal outside the physical columns or listed twice is refused")]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 3 })]
    [InlineData(new[] { 1, 1 })]
    public void Layout_InvalidDroppedOrdinals_ShouldBeRefused(int[] dropped)
    {
        Should.Throw<ArgumentException>(() => new SqlCatalogTable(ObjectId, "dbo", "t", [Id, Amount], droppedColumnOrdinals: dropped))
            .ParamName.ShouldBe("droppedColumnOrdinals");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a version written after a drop stores NULL at the dropped ordinal")]
    public void Encode_AfterDrop_ShouldStoreNullAtTheDroppedOrdinal()
    {
        // Act
        byte[] record = SqlRowCodec.Encode(NoteDropped, [7, 12.50m, TokenValue], new TransactionSequence(5));

        // Assert: byte-identical to the version the created definition writes with a NULL note.
        record.ShouldBe(SqlRowCodec.Encode(Created, [7, null, 12.50m, TokenValue], new TransactionSequence(5)));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a version stores nothing past its last live column, and a later column reads as its missing tail")]
    public void Encode_AfterTrailingDrop_ShouldStoreNothingPastTheLastLiveColumn()
    {
        // Arrange: the re-added note (physical 4) dropped again, then another column added at 5.
        var extra = new SqlCatalogColumn("extra", new DatabaseTypeInfo(DatabaseType.Int32));
        var extraAdded = new SqlCatalogTable(ObjectId, "dbo", "t", [Id, Amount, Token, extra], droppedColumnOrdinals: [1, 4]);

        // Act
        byte[] record = SqlRowCodec.Encode(BothDropped, [4, 5.00m, TokenValue], new TransactionSequence(6));

        // Assert: no component for the trailing dropped ordinal, so the version is the one the
        // layout without it writes, and a column added afterwards is the version's missing tail.
        record.ShouldBe(SqlRowCodec.Encode(NoteDropped, [4, 5.00m, TokenValue], new TransactionSequence(6)));
        Decode(record, BothDropped, out int stored).ShouldBe(new object?[] { 4, 5.00m, TokenValue });
        stored.ShouldBe(3);
        Decode(record, extraAdded, out stored).ShouldBe(new object?[] { 4, 5.00m, TokenValue, null });
        stored.ShouldBe(3, "extra was added after the version was written, so it reads its default");

        byte[] withExtra = SqlRowCodec.Encode(extraAdded, [4, 5.00m, TokenValue, 11], new TransactionSequence(7));
        withExtra.Length.ShouldBe(record.Length + 1 + 5, "a one-byte NULL at the now-inner dropped ordinal 4, then extra's INT");
        Decode(withExtra, extraAdded, out stored).ShouldBe(new object?[] { 4, 5.00m, TokenValue, 11 });
        stored.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: every definition decodes every version of the table onto the right columns")]
    public void Decode_EveryDefinitionOverEveryVersion_ShouldReadEachValueInItsColumn()
    {
        // Arrange: one version written under each definition.
        byte[] beforeDrop = SqlRowCodec.Encode(Created, [1, "dropped text", 1.25m, TokenValue], new TransactionSequence(3));
        byte[] afterDrop = SqlRowCodec.Encode(NoteDropped, [2, 2.50m, TokenValue], new TransactionSequence(4));
        byte[] afterReAdd = SqlRowCodec.Encode(NoteReadded, [3, 3.75m, TokenValue, 99L], new TransactionSequence(5));
        byte[] afterBoth = SqlRowCodec.Encode(BothDropped, [4, 5.00m, TokenValue], new TransactionSequence(6));

        // Act / Assert: the dropped note's value is never read again, the re-added note
        // never reads it, and a definition bound before a drop reads nothing for the dropped
        // column from a later version: a stored NULL, or the missing tail past a trailing
        // drop. (No statement decodes that pairing: its snapshot predates its binding.)
        Decode(beforeDrop, Created).ShouldBe(new object?[] { 1, "dropped text", 1.25m, TokenValue });
        Decode(afterDrop, Created).ShouldBe(new object?[] { 2, null, 2.50m, TokenValue });
        Decode(afterReAdd, Created).ShouldBe(new object?[] { 3, null, 3.75m, TokenValue });

        Decode(beforeDrop, NoteDropped).ShouldBe(new object?[] { 1, 1.25m, TokenValue });
        Decode(afterDrop, NoteDropped).ShouldBe(new object?[] { 2, 2.50m, TokenValue });
        Decode(afterReAdd, NoteDropped).ShouldBe(new object?[] { 3, 3.75m, TokenValue });
        Decode(afterBoth, NoteDropped).ShouldBe(new object?[] { 4, 5.00m, TokenValue });

        Decode(beforeDrop, NoteReadded, out int stored).ShouldBe(new object?[] { 1, 1.25m, TokenValue, null });
        stored.ShouldBe(3, "the re-added note is part of the missing tail of a version written before it");
        Decode(afterReAdd, NoteReadded, out stored).ShouldBe(new object?[] { 3, 3.75m, TokenValue, 99L });
        stored.ShouldBe(4);
        Decode(afterBoth, NoteReadded, out stored).ShouldBe(new object?[] { 4, 5.00m, TokenValue, null });
        stored.ShouldBe(3, "a version written after the second drop stores nothing past token, its last live column");

        Decode(afterReAdd, BothDropped).ShouldBe(new object?[] { 3, 3.75m, TokenValue });
        Decode(afterBoth, BothDropped).ShouldBe(new object?[] { 4, 5.00m, TokenValue });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a dropped component is skipped without being read as a value, stamps preserved")]
    public void Decode_DroppedComponent_ShouldBeSkippedWithTheStamps()
    {
        // Arrange: a tombstoned version whose dropped component is a long string.
        byte[] record = SqlRowCodec.WithDeleter(
            SqlRowCodec.Encode(Created, [7, new string('x', 100_000), 12.50m, TokenValue], new TransactionSequence(5)),
            new TransactionSequence(9));

        // Act
        var values = SqlRowCodec.Decode(record, NoteDropped, out var writer, out var deleter, out int stored);

        // Assert
        values.ShouldBe(new object?[] { 7, 12.50m, TokenValue });
        writer.ShouldBe(new TransactionSequence(5));
        deleter.ShouldBe(new TransactionSequence(9));
        stored.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a version that ends inside the dropped columns stores only its leading live columns")]
    public void Decode_VersionEndingAtADroppedOrdinal_ShouldCountOnlyStoredLiveColumns()
    {
        // Arrange: written when the table was (id, note).
        var original = new SqlCatalogTable(ObjectId, "dbo", "t", [Id, Note]);
        byte[] record = SqlRowCodec.Encode(original, [7, "n"], new TransactionSequence(3));
        var noteDroppedAmountAdded = new SqlCatalogTable(ObjectId, "dbo", "t", [Id, Amount], droppedColumnOrdinals: [1]);

        // Act
        var values = SqlRowCodec.Decode(record, noteDroppedAmountAdded, out _, out _, out int stored);

        // Assert: amount is the missing tail, not the dropped note's value.
        values.ShouldBe(new object?[] { 7, null });
        stored.ShouldBe(1);
    }

    /// <summary>
    /// Every record a decode meets comes from the table's own page chain, so a record of another
    /// object, or one too short for its stamps, is damaged rather than skipped (#1362). Before,
    /// both decoded as "not this table's" and the executor dropped the row.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a record of another object or without a stamp header fails to decode (#1362)")]
    public void Decode_ForeignOrShortRecord_ShouldThrow()
    {
        // Arrange
        byte[] record = SqlRowCodec.Encode(Created, [7, "n", 1m, TokenValue], new TransactionSequence(3));
        var other = new SqlCatalogTable(ObjectId + 1, "dbo", "other", [Id, Note, Amount, Token]);

        // Act
        var foreign = Should.Throw<DatabaseTypeException>(() => SqlRowCodec.Decode(record, other, out _, out _, out _));
        var shortRecord = Should.Throw<DatabaseTypeException>(
            () => SqlRowCodec.Decode(record.AsSpan(0, SqlRowCodec.StampHeaderSize - 1), Created, out _, out _, out _));
        var prefixOnly = Should.Throw<DatabaseTypeException>(
            () => SqlRowCodec.Decode(record.AsSpan(0, SqlRowCodec.StampHeaderSize), Created, out _, out _, out _));

        // Assert
        foreign.Message.ShouldContain($"names object {ObjectId}, not the table's object {ObjectId + 1}", Case.Sensitive);
        shortRecord.Message.ShouldContain("fewer than its 16-byte version-stamp header", Case.Sensitive);
        prefixOnly.Message.ShouldContain("no further components", Case.Sensitive);
    }

    /// <summary>
    /// A component whose tag names a well-formed value of another type is not a version the
    /// engine wrote: every value is coerced to its column's type before it is encoded. Before
    /// #1362 the decode read whatever type the tag named, so an INT column produced a float.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a component of another type than its column's fails to decode (#1362)")]
    public void Decode_ComponentOfAnotherType_ShouldThrow()
    {
        // Arrange: id's INT tag rewritten as a REAL's, which has the same four-byte width.
        byte[] record = SqlRowCodec.Encode(Created, [7, "n", 1m, TokenValue], new TransactionSequence(3));
        record[IdComponentOffset].ShouldBe((byte)DatabaseType.Int32);
        record[IdComponentOffset] = (byte)DatabaseType.Float32;

        // Act
        var failure = Should.Throw<DatabaseTypeException>(() => SqlRowCodec.Decode(record, Created, out _, out _, out _));

        // Assert
        failure.Message.ShouldBe("Expected a Int32 component but found Float32.");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a malformed or truncated component fails to decode, a missing tail does not (#1362)")]
    public void Decode_MalformedOrTruncatedComponent_ShouldThrow()
    {
        // Arrange
        byte[] record = SqlRowCodec.Encode(Created, [7, "n", 1m, TokenValue], new TransactionSequence(3));
        byte[] invalidTag = record.ToArray();
        invalidTag[IdComponentOffset] = 0xEE;

        // Act
        var malformed = Should.Throw<DatabaseTypeException>(() => SqlRowCodec.Decode(invalidTag, Created, out _, out _, out _));
        var truncated = Should.Throw<DatabaseTypeException>(
            () => SqlRowCodec.Decode(record.AsSpan(0, IdComponentOffset + 3), Created, out _, out _, out _));
        var missingTail = SqlRowCodec.Decode(record.AsSpan(0, IdComponentOffset + 5), Created, out _, out _, out int stored);

        // Assert: a record that ends between components is a version written before the
        // columns it lacks; one that ends inside a component is damaged.
        malformed.Message.ShouldBe("Expected a Int32 component but found 238.");
        truncated.Message.ShouldBe("Malformed key: truncated component.");
        missingTail.ShouldBe(new object?[] { 7, null, null, null });
        stored.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Row codec: a well-formed component whose value is outside its type fails to decode (#1362)")]
    public void Decode_ValueOutsideItsType_ShouldThrow()
    {
        // Arrange: a DATE whose stored day number is past DateOnly.MaxValue.
        var day = new SqlCatalogColumn("day", new DatabaseTypeInfo(DatabaseType.Date));
        var table = new SqlCatalogTable(ObjectId, "dbo", "days", [day]);
        byte[] record = SqlRowCodec.Encode(table, [new DateOnly(2026, 10, 9)], new TransactionSequence(3));
        int dayNumber = IdComponentOffset + 1;
        record.AsSpan(dayNumber, 4).Fill(0xFF);

        // Act
        var failure = Should.Throw<DatabaseTypeException>(() => SqlRowCodec.Decode(record, table, out _, out _, out _));

        // Assert
        failure.Message.ShouldStartWith("A component holds a value outside its type: ", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<ArgumentOutOfRangeException>();
    }

    /// <summary>The offset of the first column's type tag: the stamps, then the object-id prefix (a tag and eight bytes).</summary>
    private const int IdComponentOffset = SqlRowCodec.StampHeaderSize + 9;

    private static object?[] Decode(byte[] record, SqlCatalogTable table) => Decode(record, table, out _);

    private static object?[] Decode(byte[] record, SqlCatalogTable table, out int stored)
        => SqlRowCodec.Decode(record, table, out _, out _, out stored);
}
