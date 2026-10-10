using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The value ABI's conversions every function call makes, a built-in's or an application's: a row
/// value converts to a <see cref="SqlValue"/> keeping its box and converts back to that same box,
/// some types inline at the call site and the rest out of line, and two values of one type compare
/// on their payload in exactly the order the engine's comparer gives their row values.
/// </summary>
public sealed class SqlValueTests
{
    /// <summary>A row value of every storage type, with the type it has: the inline conversions and the out-of-line ones.</summary>
    public static TheoryData<object, DatabaseType> RowValues => new()
    {
        { "text", DatabaseType.String },
        { 42L, DatabaseType.Int64 },
        { 42, DatabaseType.Int32 },
        { 4.25m, DatabaseType.Decimal },
        { 4.25d, DatabaseType.Float64 },
        { true, DatabaseType.Boolean },
        { (short)42, DatabaseType.Int16 },
        { (sbyte)42, DatabaseType.Int8 },
        { 4.25f, DatabaseType.Float32 },
        { new byte[] { 1, 2 }, DatabaseType.Binary },
        { new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc), DatabaseType.DateTime },
        { new DateTimeOffset(2026, 10, 10, 1, 2, 3, TimeSpan.FromHours(2)), DatabaseType.DateTimeOffset },
        { new DateOnly(2026, 10, 10), DatabaseType.Date },
        { new TimeOnly(1, 2, 3), DatabaseType.Time },
        { TimeSpan.FromMinutes(90), DatabaseType.TimeSpan },
        { Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), DatabaseType.Guid },
    };

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SqlValue: a row value keeps its box through a call")]
    [MemberData(nameof(RowValues))]
    public void FromObject_RowValue_ShouldKeepItsBox(object value, DatabaseType type)
    {
        // Act
        var converted = SqlValue.FromObject(value);

        // Assert
        converted.Type.ShouldBe(type);
        converted.ToObject().ShouldBeSameAs(value);
        converted.ShouldBe(Factory(value));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SqlValue: a NULL row value is SQL NULL both ways")]
    public void FromObject_Null_ShouldConvertToNullAndBack()
    {
        // Act
        var converted = SqlValue.FromObject(null);

        // Assert
        converted.IsNull.ShouldBeTrue();
        converted.ShouldBe(SqlValue.Null);
        converted.ToObject().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SqlValue: a CLR type the codec does not store widens without its box")]
    public void FromObject_WideningClrType_ShouldConvertToTheStorageType()
    {
        // Arrange
        object[] values = [(byte)7, (ushort)7, 7u, 7ul, 'x'];

        // Act
        var converted = Array.ConvertAll(values, SqlValue.FromObject);

        // Assert
        converted[0].ShouldBe(SqlValue.FromInt16(7));
        converted[1].ShouldBe(SqlValue.FromInt32(7));
        converted[2].ShouldBe(SqlValue.FromInt64(7));
        converted[3].ShouldBe(SqlValue.FromDecimal(7m));
        converted[4].ShouldBe(SqlValue.FromString("x"));
        converted[0].ToObject().ShouldBe((short)7);
        converted[3].ToObject().ShouldBe(7m);
        converted[4].ToObject().ShouldBe("x");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SqlValue: a computed value boxes once, a BOOLEAN into a shared box")]
    public void ToObject_ComputedValue_ShouldBoxItsPayload()
    {
        // Arrange
        string text = "computed";

        // Act
        object? number = SqlValue.FromInt64(-5).ToObject();
        object? first = SqlValue.FromBoolean(true).ToObject();
        object? second = SqlValue.FromBoolean(true).ToObject();
        object? falseValue = SqlValue.FromBoolean(false).ToObject();

        // Assert
        number.ShouldBe(-5L);
        first.ShouldBe(true);
        first.ShouldBeSameAs(second);
        falseValue.ShouldBe(false);
        SqlValue.FromString(text).ToObject().ShouldBeSameAs(text);
        SqlValue.FromDecimal(1.50m).ToObject().ShouldBe(1.50m);
        SqlValue.FromGuid(Guid.Empty).ToObject().ShouldBe(Guid.Empty);
    }

    /// <summary>
    /// Pairs of one type each, including the cases the order defines specially: NaN below every
    /// number and equal to itself, signed zeros equal, a TIMESTAMP by its ticks whatever its kind, a
    /// TIMESTAMPTZ by its instant whatever its offset, binary by content and length, text under a
    /// collation, and the extremes of SMALLINT and TINYINT, whose own comparisons return the
    /// difference of the two values where the comparer returns -1 or 1.
    /// </summary>
    public static TheoryData<object, object, string?> SameTypePairs => new()
    {
        { "abc", "abd", null },
        { "abc", "ABC", "case_insensitive" },
        { "abc", "ABC", null },
        { long.MinValue, long.MaxValue, null },
        { 3L, 3L, null },
        { -1, 1, null },
        { 2.50m, 2.5m, null },
        { -0.01m, 0.01m, null },
        { double.NaN, double.NegativeInfinity, null },
        { double.NaN, double.NaN, null },
        { -0.0d, 0.0d, null },
        { 1.5d, -1.5d, null },
        { float.NaN, 1f, null },
        { (short)-3, (short)3, null },
        { short.MinValue, short.MaxValue, null },
        { (sbyte)5, (sbyte)-5, null },
        { sbyte.MinValue, sbyte.MaxValue, null },
        { false, true, null },
        { new byte[] { 1, 2 }, new byte[] { 1, 2, 0 }, null },
        { new byte[] { 2 }, new byte[] { 1, 9 }, null },
        { new DateOnly(2026, 1, 2), new DateOnly(2025, 12, 31), null },
        { new TimeOnly(10, 0), new TimeOnly(10, 0), null },
        { new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local), null },
        { new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), null },
        { new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null },
        { new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null },
        { TimeSpan.FromSeconds(-1), TimeSpan.Zero, null },
        { Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000001"), null },
    };

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SqlValue: values of one type compare in the engine's order, with its result")]
    [MemberData(nameof(SameTypePairs))]
    public void Compare_SameType_ShouldMatchTheEngineOrder(object left, object right, string? collationName)
    {
        // Arrange
        var collation = collationName is null ? null : Collation.FromName(collationName);
        var first = SqlValue.FromObject(left);
        var second = SqlValue.FromObject(right);

        // Act
        int forward = SqlValue.Compare(first, second, collation);
        int backward = SqlValue.Compare(second, first, collation);
        int self = SqlValue.Compare(first, first, collation);

        // Assert: the comparer's own result, not only its sign. Compare returned exactly that before
        // it compared payloads, and an application's aggregate may test for -1 or 1.
        forward.ShouldBe(SqlValueComparer.Compare(left, right, collation));
        backward.ShouldBe(SqlValueComparer.Compare(right, left, collation));
        Math.Sign(backward).ShouldBe(-Math.Sign(forward));
        self.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SqlValue: values of two types compare through the engine's comparer")]
    public void Compare_AcrossTypes_ShouldCompareNumbersByValueAndRefuseTheRest()
    {
        // Act
        int integerToDecimal = SqlValue.Compare(SqlValue.FromInt32(3), SqlValue.FromDecimal(2.5m));
        int realToDouble = SqlValue.Compare(SqlValue.FromSingle(0.5f), SqlValue.FromDouble(0.5));
        int bigintToDouble = SqlValue.Compare(SqlValue.FromInt64(1), SqlValue.FromDouble(double.NaN));
        var refused = Should.Throw<DatabaseException>(() => SqlValue.Compare(SqlValue.FromString("1"), SqlValue.FromInt64(1)));

        // Assert
        integerToDecimal.ShouldBeGreaterThan(0);
        realToDouble.ShouldBe(0);
        bigintToDouble.ShouldBeGreaterThan(0);
        refused.Message.ShouldBe("Cannot compare values of types String and Int64.");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SqlValue: NULL has no place in the order")]
    public void Compare_Null_ShouldNameTheNullOperand()
    {
        // Act
        var left = Should.Throw<ArgumentException>(() => SqlValue.Compare(SqlValue.Null, SqlValue.FromInt32(1)));
        var right = Should.Throw<ArgumentException>(() => SqlValue.Compare(SqlValue.FromString("a"), SqlValue.Null));
        var both = Should.Throw<ArgumentException>(() => SqlValue.Compare(SqlValue.Null, SqlValue.Null));

        // Assert
        left.ParamName.ShouldBe("left");
        right.ParamName.ShouldBe("right");
        both.ParamName.ShouldBe("left");
        left.Message.ShouldStartWith("NULL has no place in the order; test IsNull first.", Case.Sensitive);
    }

    // The public factory of the value's type, which a function uses; the conversion must agree with it.
    private static SqlValue Factory(object value) => value switch
    {
        string text => SqlValue.FromString(text),
        long number => SqlValue.FromInt64(number),
        int number => SqlValue.FromInt32(number),
        decimal number => SqlValue.FromDecimal(number),
        double number => SqlValue.FromDouble(number),
        bool flag => SqlValue.FromBoolean(flag),
        short number => SqlValue.FromInt16(number),
        sbyte number => SqlValue.FromSByte(number),
        float number => SqlValue.FromSingle(number),
        byte[] bytes => SqlValue.FromBinary(bytes),
        DateTime moment => SqlValue.FromDateTime(moment),
        DateTimeOffset moment => SqlValue.FromDateTimeOffset(moment),
        DateOnly date => SqlValue.FromDate(date),
        TimeOnly time => SqlValue.FromTime(time),
        TimeSpan span => SqlValue.FromTimeSpan(span),
        Guid id => SqlValue.FromGuid(id),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
