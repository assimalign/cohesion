using System;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubStoredRecordTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the record and its version")]
    public void Constructor_WithARecordAndVersion_ShouldExposeThem()
    {
        // Arrange
        var record = new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty);

        // Act
        var stored = new IdentityHubStoredRecord(record, "42");

        // Assert
        stored.Record.ShouldBeSameAs(record);
        stored.Version.Value.ShouldBe("42");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a null record or a default version")]
    public void Constructor_WithANullRecordOrDefaultVersion_ShouldThrow()
    {
        // Arrange
        var record = new IdentityHubRecord("user-1", ReadOnlyMemory<byte>.Empty);

        // Act
        ArgumentNullException nullRecord = Should.Throw<ArgumentNullException>(() => new IdentityHubStoredRecord(null!, "42"));
        ArgumentException noVersion = Should.Throw<ArgumentException>(() => new IdentityHubStoredRecord(record, default));

        // Assert
        nullRecord.ParamName.ShouldBe("record");
        noVersion.ParamName.ShouldBe("version");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ToString: Should name the key and version and never the payload")]
    public void ToString_OfAStoredRecord_ShouldReturnTheKeyAndVersion()
    {
        // Arrange
        var stored = new IdentityHubStoredRecord(
            new IdentityHubRecord("user-1", Encoding.UTF8.GetBytes("verifier-digest")),
            "42");

        // Act
        string text = stored.ToString();

        // Assert
        text.ShouldBe("user-1 @ 42");
    }
}
