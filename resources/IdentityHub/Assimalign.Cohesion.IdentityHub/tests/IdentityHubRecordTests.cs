using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordTests
{
    private static readonly IdentityHubRecordIndexName _alias = "alias";
    private static readonly IdentityHubRecordIndexName _owner = "owner";

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the key, payload, expiry, and index entries")]
    public void Constructor_WithEveryPart_ShouldExposeThem()
    {
        // Arrange
        var expiresAt = new DateTimeOffset(2026, 3, 1, 8, 30, 0, TimeSpan.Zero);
        IdentityHubRecordIndex[] indexes = [new(_alias, "alice", isUnique: true), new(_owner, "tenant-1")];

        // Act
        var record = new IdentityHubRecord("user-1", Encoding.UTF8.GetBytes("payload"), expiresAt, indexes);

        // Assert
        record.Key.Value.ShouldBe("user-1");
        Encoding.UTF8.GetString(record.Payload.Span).ShouldBe("payload");
        record.ExpiresAt.ShouldBe(expiresAt);
        record.Indexes.ShouldBe(indexes);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should default to no expiry and no index entries")]
    public void Constructor_WithOnlyAKeyAndPayload_ShouldHaveNoExpiryAndNoIndexes()
    {
        // Act
        var record = new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty);
        var none = new IdentityHubRecord("user-2", ReadOnlyMemory<byte>.Empty, indexes: Array.Empty<IdentityHubRecordIndex>());

        // Assert
        record.ExpiresAt.ShouldBeNull();
        record.Indexes.ShouldBeEmpty();
        record.Payload.Length.ShouldBe(0);
        none.Indexes.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a default key")]
    public void Constructor_WithADefaultKey_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new IdentityHubRecord(default, ReadOnlyMemory<byte>.Empty));

        // Assert
        exception.ParamName.ShouldBe("key");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept the largest payload and refuse one byte more")]
    public void Constructor_WithAPayloadOverTheLimit_ShouldThrowArgumentException()
    {
        // Arrange
        byte[] largest = new byte[IdentityHubRecord.MaxPayloadLength];
        byte[] tooLarge = new byte[IdentityHubRecord.MaxPayloadLength + 1];

        // Act
        var record = new IdentityHubRecord("user-1", largest);
        ArgumentException exception = Should.Throw<ArgumentException>(() => new IdentityHubRecord("user-1", tooLarge));

        // Assert
        record.Payload.Length.ShouldBe(IdentityHubRecord.MaxPayloadLength);
        exception.ParamName.ShouldBe("payload");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should copy the payload")]
    public void Constructor_WhenTheCallerChangesItsBufferAfterwards_ShouldKeepThePayload()
    {
        // Arrange
        byte[] buffer = Encoding.UTF8.GetBytes("payload");
        var record = new IdentityHubRecord("user-1", buffer);

        // Act
        Array.Clear(buffer);

        // Assert
        Encoding.UTF8.GetString(record.Payload.Span).ShouldBe("payload");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should snapshot the index entries")]
    public void Constructor_WhenTheCallerChangesItsListAfterwards_ShouldKeepTheIndexEntries()
    {
        // Arrange
        var indexes = new List<IdentityHubRecordIndex> { new(_alias, "alice", isUnique: true) };
        var record = new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, indexes: indexes);

        // Act
        indexes.Add(new IdentityHubRecordIndex(_owner, "tenant-1"));
        indexes[0] = new IdentityHubRecordIndex(_alias, "mallory", isUnique: true);

        // Assert
        record.Indexes.ShouldBe([new IdentityHubRecordIndex(_alias, "alice", isUnique: true)]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the expiry as the same instant in UTC")]
    public void Constructor_WithAnExpiryAtAnOffset_ShouldNormalizeItToUtc()
    {
        // Arrange
        var local = new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.FromHours(2));

        // Act
        var record = new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, local);

        // Assert
        DateTimeOffset expiresAt = record.ExpiresAt.ShouldNotBeNull();
        expiresAt.Offset.ShouldBe(TimeSpan.Zero);
        expiresAt.UtcDateTime.ShouldBe(local.UtcDateTime);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept the most index entries and refuse one more")]
    public void Constructor_WithMoreIndexEntriesThanTheLimit_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityHubRecordIndex[] most = Enumerable.Range(0, IdentityHubRecord.MaxIndexCount)
            .Select(number => new IdentityHubRecordIndex(_alias, $"alias-{number}"))
            .ToArray();
        IdentityHubRecordIndex[] tooMany = [.. most, new IdentityHubRecordIndex(_alias, "one-more")];

        // Act
        var record = new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, indexes: most);
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, indexes: tooMany));

        // Assert
        record.Indexes.Count.ShouldBe(IdentityHubRecord.MaxIndexCount);
        exception.ParamName.ShouldBe("indexes");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a default index entry")]
    public void Constructor_WithADefaultIndexEntry_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityHubRecordIndex[] indexes = [new(_alias, "alice"), default];

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, indexes: indexes));

        // Assert
        exception.ParamName.ShouldBe("indexes");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse the same index entry twice")]
    public void Constructor_WithTheSameIndexEntryTwice_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityHubRecordIndex[] indexes = [new(_alias, "alice"), new(_owner, "tenant-1"), new(_alias, "alice")];

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, indexes: indexes));

        // Assert
        exception.ParamName.ShouldBe("indexes");
        exception.Message.ShouldContain("alias", Case.Sensitive);
        exception.Message.ShouldNotContain("alice");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse one index as both unique and not unique")]
    public void Constructor_WithOneIndexAsBothUniqueAndNotUnique_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityHubRecordIndex[] indexes = [new(_alias, "alice", isUnique: true), new(_alias, "alice@example.com")];

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, indexes: indexes));

        // Assert
        exception.ParamName.ShouldBe("indexes");
        exception.Message.ShouldContain("alias", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept one index listed with several values")]
    public void Constructor_WithOneIndexListedWithSeveralValues_ShouldKeepEveryEntry()
    {
        // Arrange
        IdentityHubRecordIndex[] indexes =
        [
            new(_alias, "alice", isUnique: true),
            new(_alias, "alice@example.com", isUnique: true),
            new(_owner, "alice"),
        ];

        // Act
        var record = new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty, indexes: indexes);

        // Assert
        record.Indexes.ShouldBe(indexes);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ToString: Should name the key and never the payload")]
    public void ToString_OfARecord_ShouldReturnTheKeyOnly()
    {
        // Arrange
        var record = new IdentityHubRecord("user-1", Encoding.UTF8.GetBytes("verifier-digest"));

        // Act
        string text = record.ToString();

        // Assert
        text.ShouldBe("user-1");
    }
}
