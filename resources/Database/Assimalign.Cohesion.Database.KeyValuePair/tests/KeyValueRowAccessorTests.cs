using System;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueRowAccessorTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Row access preserves conversions, nulls and failures")]
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
        absent.Message.ShouldBe("Field 0 is NULL.");
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
        row.GetBytes(4).ToArray().ShouldBe(new byte[] { 1, 2 });
        var bytesFailure = Should.Throw<DatabaseException>(() => row.GetBytes(1));
        bytesFailure.Message.ShouldBe("Field 1 is not binary.");
        bytesFailure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK02001");
        Should.Throw<FormatException>(() => row.GetInt32(6));
        Should.Throw<FormatException>(() => row.GetBoolean(1));
        Should.Throw<OverflowException>(() => row.GetInt32(5));
        Should.Throw<InvalidCastException>(() => row.GetInt64(7));
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Every ordinal row accessor uses the base bounds guard")]
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

    private static DatabaseRow CreateRow(object?[] values) => new KeyValueMaterializedRow(values);
}
