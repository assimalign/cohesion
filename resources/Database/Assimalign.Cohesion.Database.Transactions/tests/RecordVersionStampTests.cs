using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Verifies the persisted 16-byte stamp contract independently of engine codecs.
/// </summary>
public class RecordVersionStampTests
{
    /// <summary>
    /// Both sequences use all eight bytes in little-endian order and payload bytes survive.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record stamps: fixed little-endian prefix preserves payload")]
    public void Stamp_FullWidthSequences_ShouldPreserveLittleEndianLayoutAndPayload()
    {
        // Arrange
        byte[] record = new byte[RecordVersionStamp.HeaderSize + 3];
        record[16] = 0x91;
        record[17] = 0x82;
        record[18] = 0x73;
        var writer = new TransactionSequence(0x0807060504030201);
        var deleter = new TransactionSequence(0x1817161514131211);

        // Act
        RecordVersionStamp.WriteWriter(record, writer);
        byte[] tombstoned = RecordVersionStamp.WithDeleter(record, deleter);
        byte[] restored = RecordVersionStamp.WithoutDeleter(tombstoned);

        // Assert
        tombstoned.ShouldBe(new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x91, 0x82, 0x73,
        });
        RecordVersionStamp.ReadStamps(tombstoned).ShouldBe((writer, deleter));
        RecordVersionStamp.ReadStamps(record).ShouldBe((writer, TransactionSequence.None));
        restored.ShouldBe(record);
        tombstoned.Length.ShouldBe(record.Length);
        restored.Length.ShouldBe(record.Length);
        ReferenceEquals(record, tombstoned).ShouldBeFalse();
        ReferenceEquals(restored, tombstoned).ShouldBeFalse();
    }

    /// <summary>
    /// Writing a writer never silently clears a tombstone or modifies a payload.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record stamps: writer update touches only writer bytes")]
    public void WriteWriter_ExistingTombstone_ShouldPreserveDeleterAndPayload()
    {
        // Arrange
        byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
        record[16] = 0xA9;
        record = RecordVersionStamp.WithDeleter(record, new TransactionSequence(53));

        // Act
        RecordVersionStamp.WriteWriter(record, new TransactionSequence(71));

        // Assert
        RecordVersionStamp.ReadStamps(record).ShouldBe((new TransactionSequence(71), new TransactionSequence(53)));
        record[16].ShouldBe((byte)0xA9);
    }

    /// <summary>
    /// Incomplete stamp prefixes retain the codec helpers' range-check failure.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Record stamps: truncated prefixes are rejected")]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    public void Stamp_TruncatedPrefix_ShouldRejectIncompleteRecord(int length)
    {
        // Arrange
        byte[] record = new byte[length];

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => RecordVersionStamp.ReadStamps(record));
        Should.Throw<ArgumentOutOfRangeException>(() => RecordVersionStamp.WithDeleter(record, new TransactionSequence(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => RecordVersionStamp.WithoutDeleter(record));
    }
}
