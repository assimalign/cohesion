using System;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentRowAccessorTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Row access preserves conversions, nulls and failures")]
    public void Accessors_CompatibleValues_ShouldRetainConversionsAndErrors()
    {
        // Arrange
        object?[] values = [null, "42", 42.5d, true, new byte[] { 1, 2 }, long.MaxValue, "bad", new object()];
        DatabaseRow row = CreateRow(values);

        // Act / Assert
        row.FieldCount.ShouldBe(values.Length);
        row.IsNull(0).ShouldBeTrue();
        row.GetValue(0).ShouldBeNull();
        row.GetString(0).ShouldBeNull();
        row.GetBytes(0).IsEmpty.ShouldBeTrue();
        var absent = Should.Throw<DatabaseException>(() => row.GetInt32(0));
        absent.Message.ShouldBe("Field 0 is null.");
        absent.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK02002");
        Should.Throw<DatabaseException>(() => row.GetInt64(0));
        Should.Throw<DatabaseException>(() => row.GetDouble(0));
        Should.Throw<DatabaseException>(() => row.GetBoolean(0));
        row.GetValue(1).ShouldBeSameAs(values[1]);
        row.GetString(1).ShouldBe("42");
        row.GetInt32(1).ShouldBe(42);
        row.GetInt64(1).ShouldBe(42);
        row.GetDouble(1).ShouldBe(42);
        row.GetInt32(2).ShouldBe(42);
        row.GetInt64(2).ShouldBe(42);
        row.GetDouble(2).ShouldBe(42.5d);
        row.GetString(2).ShouldBe("42.5");
        row.GetBoolean(2).ShouldBeTrue();
        row.GetBoolean(3).ShouldBeTrue();
        row.GetInt32(3).ShouldBe(1);
        row.GetString(3).ShouldBe("True");
        var bytesFailure = Should.Throw<DatabaseException>(() => row.GetBytes(4));
        Encoding.UTF8.GetString(row.GetBytes(1).Span).ShouldBe("42");
        bytesFailure.Message.ShouldBe("Field 4 is not JSON or a string.");
        bytesFailure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK02001");
        Should.Throw<FormatException>(() => row.GetInt32(6));
        Should.Throw<FormatException>(() => row.GetBoolean(1));
        Should.Throw<OverflowException>(() => row.GetInt32(5));
        Should.Throw<InvalidCastException>(() => row.GetInt64(7));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Row access retains MISSING, JSON NULL and bare bytes")]
    public void Accessors_CompatibleValues_ShouldRetainMissingAndJson()
    {
        // Arrange
        using var json = JsonDocument.Parse("{\"a\":1}");
        using var nullJson = JsonDocument.Parse("null");
        DatabaseRow row = CreateRow([DocumentMissing.Instance, json.RootElement, nullJson.RootElement]);

        // Act / Assert
        row.IsMissing(0).ShouldBeTrue();
        row.IsNull(0).ShouldBeFalse();
        row.GetValue(0).ShouldBeNull();
        row.GetString(0).ShouldBeNull();
        row.GetBytes(0).IsEmpty.ShouldBeTrue();
        Should.Throw<DatabaseException>(() => row.GetInt32(0));
        row.GetString(1).ShouldBe("{\"a\":1}");
        Encoding.UTF8.GetString(row.GetBytes(1).Span).ShouldBe("{\"a\":1}");
        Should.Throw<InvalidCastException>(() => row.GetInt32(1));
        row.IsNull(2).ShouldBe(false);
        row.GetString(2).ShouldBe("null");
        Should.Throw<InvalidCastException>(() => row.GetDouble(2));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Bare row bytes retain their original JSON spelling")]
    public void Accessors_CompatibleValues_ShouldRetainBareBytes()
    {
        // Arrange
        byte[] raw = Encoding.UTF8.GetBytes(" \"value\" ");
        DatabaseRow row = new DocumentQueryResult.DocumentQueryRow(["value"], raw, bareValue: true);

        // Act / Assert
        row.GetBytes(0).ToArray().ShouldBe(raw);
        row.GetString(0).ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Raw bytes cannot bypass an empty row's bounds")]
    public void Accessors_EmptyRawRow_ShouldUseBaseGuard()
    {
        // Arrange
        byte[] raw = Encoding.UTF8.GetBytes("{}");
        DatabaseRow row = new DocumentQueryResult.DocumentQueryRow([], raw);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => row.GetBytes(0)).ParamName.ShouldBe("ordinal");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Every ordinal row accessor uses the base bounds guard")]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Accessors_InvalidOrdinal_ShouldUseBaseGuard(int ordinal)
    {
        // Arrange
        DatabaseRow row = CreateRow(["value"]);
        Action[] accessors = [() => row.IsNull(ordinal), () => row.IsMissing(ordinal),
            () => row.GetValue(ordinal), () => row.GetBytes(ordinal), () => row.GetString(ordinal),
            () => row.GetInt32(ordinal), () => row.GetInt64(ordinal),
            () => row.GetBoolean(ordinal), () => row.GetDouble(ordinal)];

        // Act / Assert
        foreach (var accessor in accessors)
        {
            Should.Throw<ArgumentOutOfRangeException>(accessor).ParamName.ShouldBe("ordinal");
        }
    }

    private static DatabaseRow CreateRow(object?[] values) => new DocumentQueryResult.DocumentQueryRow(values, default);
}
