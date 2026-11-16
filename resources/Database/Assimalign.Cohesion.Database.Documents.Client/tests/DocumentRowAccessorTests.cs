using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentRowAccessorTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents.Client] - Row access preserves conversions, nulls and failures")]
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
        var absent = Should.Throw<InvalidOperationException>(() => row.GetInt32(0));
        absent.Message.ShouldBe("The field is NULL or MISSING.");
        Should.Throw<InvalidOperationException>(() => row.GetInt64(0));
        Should.Throw<InvalidOperationException>(() => row.GetDouble(0));
        Should.Throw<InvalidOperationException>(() => row.GetBoolean(0));
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
        var bytesFailure = Should.Throw<InvalidOperationException>(() => row.GetBytes(4));
        Encoding.UTF8.GetString(row.GetBytes(1).Span).ShouldBe("42");
        bytesFailure.Message.ShouldBe("The field is not JSON or a string.");
        Should.Throw<FormatException>(() => row.GetInt32(6));
        Should.Throw<FormatException>(() => row.GetBoolean(1));
        Should.Throw<OverflowException>(() => row.GetInt32(5));
        Should.Throw<InvalidCastException>(() => row.GetInt64(7));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Client] - Row access retains MISSING, JSON NULL and bare bytes")]
    public void Accessors_CompatibleValues_ShouldRetainMissingAndJson()
    {
        // Arrange
        using var json = JsonDocument.Parse("{\"a\":1}");
        using var nullJson = JsonDocument.Parse("null");
        DatabaseRow row = CreateRow([default(JsonElement), json.RootElement, nullJson.RootElement]);

        // Act / Assert
        row.IsMissing(0).ShouldBeTrue();
        row.IsNull(0).ShouldBeFalse();
        row.GetValue(0).ShouldBeNull();
        row.GetString(0).ShouldBeNull();
        row.GetBytes(0).IsEmpty.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => row.GetInt32(0));
        row.GetString(1).ShouldBe("{\"a\":1}");
        Encoding.UTF8.GetString(row.GetBytes(1).Span).ShouldBe("{\"a\":1}");
        Should.Throw<InvalidCastException>(() => row.GetInt32(1));
        row.IsNull(2).ShouldBe(true);
        row.GetString(2).ShouldBe("null");
        Should.Throw<InvalidCastException>(() => row.GetDouble(2));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Client] - Bare row bytes retain their original JSON spelling")]
    public void Accessors_CompatibleValues_ShouldRetainBareBytes()
    {
        // Arrange
        byte[] raw = Encoding.UTF8.GetBytes(" \"value\" ");
        DatabaseRow row = new DocumentRow(["value"], new Dictionary<string, int>(), raw);

        // Act / Assert
        row.GetBytes(0).ToArray().ShouldBe(raw);
        row.GetString(0).ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Client] - Raw bytes cannot bypass an empty row's bounds")]
    public void Accessors_EmptyRawRow_ShouldUseBaseGuard()
    {
        // Arrange
        byte[] raw = Encoding.UTF8.GetBytes("{}");
        DatabaseRow row = new DocumentRow([], new Dictionary<string, int>(), raw);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => row.GetBytes(0)).ParamName.ShouldBe("ordinal");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Client] - Ordinal indexers use the base row guard")]
    public void Indexer_InvalidOrdinal_ShouldUseBaseGuard()
    {
        // Arrange
        var row = new DocumentRow(["value"], new Dictionary<string, int>());

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => { _ = row[-1]; }).ParamName.ShouldBe("ordinal");
        Should.Throw<ArgumentOutOfRangeException>(() => { _ = row[1]; }).ParamName.ShouldBe("ordinal");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents.Client] - Every ordinal row accessor uses the base bounds guard")]
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

    private static DatabaseRow CreateRow(object?[] values) => new DocumentRow(values, new Dictionary<string, int>());
}
