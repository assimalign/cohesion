using System;
using System.Globalization;
using System.Linq.Expressions;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

/// <summary>The retained builder turns CLR values into SQL literals without culture or quoting drift.</summary>
public sealed class TypedSchemaDefaultTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: strings and chars escape SQL quotes and retain ordinary backslashes")]
    public void Default_TextValues_ShouldQuoteAndDoubleQuotes()
    {
        // Arrange
        const string text = "it's \\ ready";

        // Act / Assert
        ShouldRender(text, "'it''s \\ ready'", DatabaseType.String);
        ShouldRender("", "''", DatabaseType.String);
        ShouldRender("'pending'", "'''pending'''", DatabaseType.String);
        ShouldRender('a', "'a'", DatabaseType.Character);
        ShouldRender('\'', "''''", DatabaseType.Character);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: numeric CLR types use invariant literals and preserve decimal scale")]
    public void Default_NumericValues_ShouldRenderInvariantValues()
    {
        // Arrange
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            // Act / Assert
            ShouldRender(sbyte.MinValue, "-128", DatabaseType.Int8);
            ShouldRender(byte.MaxValue, "255", DatabaseType.Int16);
            ShouldRender(short.MinValue, "-32768", DatabaseType.Int16);
            ShouldRender(int.MinValue, "-2147483648", DatabaseType.Int32);
            ShouldRender(long.MinValue, "-9223372036854775808", DatabaseType.Int64);
            ShouldRender(-1.25f, "-1.25", DatabaseType.Float32);
            ShouldRender(-1.25d, "-1.25", DatabaseType.Float64);
            ShouldRender(float.Epsilon, "1E-45", DatabaseType.Float32);
            ShouldRender(double.Epsilon, "5E-324", DatabaseType.Float64);
            ShouldRender(-1234.500m, "-1234.500", DatabaseType.Decimal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: booleans and nullable values render literal keywords")]
    public void Default_BooleanAndNullValues_ShouldRenderKeywords()
    {
        // Arrange
        int? quantity = null;
        string? label = null;

        // Act / Assert
        ShouldRender(true, "TRUE", DatabaseType.Boolean);
        ShouldRender(false, "FALSE", DatabaseType.Boolean);
        ShouldRender(quantity, "NULL", DatabaseType.Int32);
        ShouldRender(label, "NULL", DatabaseType.String);
        ShouldRender<int?>(7, "7", DatabaseType.Int32);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: nullable selector inference accepts an ordinary value and null")]
    public void Default_NullableSelector_ShouldInferValueType()
    {
        // Arrange / Act
        SqlCompiledSchema populated = SqlSchema.Compile("defaults", schema => schema.Table<ValueRow<int?>>("items", table =>
            table.Default(row => row.Value, 7)));
        SqlCompiledSchema absent = SqlSchema.Compile("defaults", schema => schema.Table<ValueRow<int?>>("items", table =>
            table.Default(row => row.Value, null)));

        // Assert
        populated.Tables[0].Columns[0].DefaultLiteral.ShouldBe("7");
        absent.Tables[0].Columns[0].DefaultLiteral.ShouldBe("NULL");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: every mapped date and time preserves its SQL value")]
    public void Default_TemporalValues_ShouldRenderInvariantText()
    {
        // Arrange
        var timestamp = new DateTime(2026, 1, 2, 13, 45, 30, DateTimeKind.Utc).AddTicks(1234567);

        // Act / Assert
        ShouldRender(new DateOnly(2026, 1, 2), "'2026-01-02'", DatabaseType.Date);
        ShouldRender(new TimeOnly(13, 45, 30).Add(TimeSpan.FromTicks(1234567)), "'13:45:30.1234567'", DatabaseType.Time);
        ShouldRender(timestamp, "'2026-01-02T13:45:30.1234567'", DatabaseType.DateTime);
        ShouldRender(new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified), TimeSpan.FromHours(-5)),
            "'2026-01-02T13:45:30.1234567-05:00'", DatabaseType.DateTimeOffset);
        ShouldRender(-new TimeSpan(2, 3, 4, 5).Add(TimeSpan.FromTicks(1234567)), "'-183845.1234567 seconds'", DatabaseType.TimeSpan);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: INTERVAL limits retain exact PostgreSQL seconds (#1422)")]
    public void Default_IntervalLimits_ShouldPreserveEveryTick()
    {
        // Arrange / Act / Assert
        ShouldRender(TimeSpan.MinValue, "'-922337203685.4775808 seconds'", DatabaseType.TimeSpan);
        ShouldRender(TimeSpan.MaxValue, "'922337203685.4775807 seconds'", DatabaseType.TimeSpan);
        ShouldRender(TimeSpan.Zero, "'0 seconds'", DatabaseType.TimeSpan);
        ShouldRender(TimeSpan.FromTicks(1), "'0.0000001 seconds'", DatabaseType.TimeSpan);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: GUIDs and binary values have deterministic literal spellings")]
    public void Default_GuidAndBinaryValues_ShouldRenderCanonicalText()
    {
        // Arrange
        var identifier = new Guid("00112233-4455-6677-8899-aabbccddeeff");

        // Act / Assert
        ShouldRender(identifier, "'00112233-4455-6677-8899-aabbccddeeff'", DatabaseType.Guid);
        ShouldRender(new byte[] { 0, 15, 128, 255 }, "'\\x000F80FF'", DatabaseType.Binary);
        ShouldRender(Array.Empty<byte>(), "'\\x'", DatabaseType.Binary);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: binary defaults snapshot the caller's mutable array")]
    public void Default_MutatedBinaryValue_ShouldRetainDeclaredBytes()
    {
        // Arrange
        byte[] bytes = [0, 255];
        SqlSchema declaration = SqlSchema.Create("defaults", schema => schema.Table<ValueRow<byte[]>>("items", table =>
            table.Default(row => row.Value, bytes)));

        // Act
        bytes[0] = 127;
        SqlCompiledSchema compiled = declaration.Compile();

        // Assert
        compiled.Tables[0].Columns[0].DefaultLiteral.ShouldBe("'\\x00FF'");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: all enum underlying types use exact numeric storage")]
    public void Default_EnumValues_ShouldRenderUnderlyingValues()
    {
        // Arrange / Act / Assert
        ShouldRender(SignedByteStatus.Value, "-128", DatabaseType.Int8);
        ShouldRender(ByteStatus.Value, "255", DatabaseType.Int16);
        ShouldRender(ShortStatus.Value, "-32768", DatabaseType.Int16);
        ShouldRender(UnsignedShortStatus.Value, "65535", DatabaseType.Int32);
        ShouldRender(Status.Pending, "7", DatabaseType.Int32);
        ShouldRender(UnsignedStatus.Value, "4294967295", DatabaseType.Int64);
        ShouldRender(LongStatus.Value, "-9223372036854775808", DatabaseType.Int64);
        ShouldRender(UnsignedLongStatus.Value, "18446744073709551615", DatabaseType.Decimal);
        ShouldRender<Status?>(Status.Pending, "7", DatabaseType.Int32);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: incompatible values and out-of-range widened selectors fail at declaration")]
    public void Default_MismatchedValue_ShouldRefuseBeforeAddingColumn()
    {
        // Arrange
        var table = new SqlTableBuilder<ValueRow<int>>("items");

        // Act
        ArgumentException mismatched = Should.Throw<ArgumentException>(() => table.Default<object>(row => row.Value, "7"));
        ArgumentException overflow = Should.Throw<ArgumentException>(() => table.Default<long>(row => row.Value, (long)int.MaxValue + 1));

        // Assert
        mismatched.ParamName.ShouldBe("value");
        overflow.ParamName.ShouldBe("value");
        table.Build().ColumnDefinitions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: nested selector conversions retain the original column type and validation")]
    public void Default_NestedSelectorConversions_ShouldUseOriginalColumnType()
    {
        // Arrange
        var table = new SqlTableBuilder<ValueRow<int>>("items");
        SqlCompiledSchema compiled = SqlSchema.Compile("defaults", schema => schema.Table<ValueRow<int>>("items", columns =>
            columns.Default<object>(row => (long)row.Value, 7)));

        // Act
        ArgumentException mismatch = Should.Throw<ArgumentException>(() => table.Default<object>(row => (long)row.Value, "7"));
        ArgumentException overflow = Should.Throw<ArgumentException>(() => table.Default<object>(row => (long)row.Value, (long)int.MaxValue + 1));
        checked
        {
            table.Default<object>(row => (short)row.Value, 7);
        }

        // Assert
        compiled.Tables[0].Columns[0].Type.ShouldBe(DatabaseType.Int32);
        compiled.Tables[0].Columns[0].DefaultLiteral.ShouldBe("7");
        mismatch.ParamName.ShouldBe("value");
        overflow.ParamName.ShouldBe("value");
        var column = table.Build().ColumnDefinitions.ShouldHaveSingleItem();
        column.ClrType.ShouldBe(typeof(int));
        column.DefaultLiteral.ShouldBe("7");
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: nonfinite numeric values fail at declaration")]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Default_NonfiniteValue_ShouldRefuseAtDeclaration(double value)
    {
        // Arrange
        var table = new SqlTableBuilder<ValueRow<double>>("items");

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => table.Default(row => row.Value, value));

        // Assert
        exception.ParamName.ShouldBe("value");
        table.Build().ColumnDefinitions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: nonfinite single precision values fail at declaration")]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Default_NonfiniteSingleValue_ShouldRefuseAtDeclaration(float value)
    {
        // Arrange
        var table = new SqlTableBuilder<ValueRow<float>>("items");

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => table.Default(row => row.Value, value));

        // Assert
        exception.ParamName.ShouldBe("value");
        table.Build().ColumnDefinitions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: selectors must be direct members and cannot be absent")]
    public void Default_InvalidSelector_ShouldRefuseAtDeclaration()
    {
        // Arrange
        var table = new SqlTableBuilder<ValueRow<int>>("items");
        Expression<Func<ValueRow<int>, int>> calculated = row => row.Value + 1;

        // Act
        ArgumentException calculatedError = Should.Throw<ArgumentException>(() => table.Default(calculated, 7));
        ArgumentNullException absentError = Should.Throw<ArgumentNullException>(() => table.Default<int>(null!, 7));

        // Assert
        calculatedError.ParamName.ShouldBe("selector");
        absentError.ParamName.ShouldBe("selector");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: raw and typed declarations with the same literal have the same document and hash")]
    public void Compile_EquivalentTypedAndRawDefaults_ShouldHaveEqualHashes()
    {
        // Arrange
        SqlCompiledSchema typed = SqlSchema.Compile("defaults", schema => schema.Table<ValueRow<string>>("items", table =>
            table.Default(row => row.Value, "it's ready")));
        SqlCompiledSchema raw = SqlSchema.Compile("defaults", schema => schema.Table<ValueRow<string>>("items", table =>
            table.DefaultSql(row => row.Value, "'it''s ready'")));

        // Act / Assert
        typed.Format.ShouldBe("cohesion/database-schema/v8");
        typed.CanonicalDocument.ShouldBe(raw.CanonicalDocument);
        typed.Hash.ShouldBe(raw.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Typed DEFAULT: matching declarations compose and conflicting declarations name the value")]
    public void Default_RepeatedDeclarations_ShouldAcceptOnlyEquivalentLiteralText()
    {
        // Arrange
        var table = new SqlTableBuilder<ValueRow<string>>("items");
        table.Default(row => row.Value, "pending");
        table.DefaultSql(row => row.Value, "'pending'");

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => table.Default(row => row.Value, "ready"));

        // Assert
        table.Build().ColumnDefinitions.ShouldHaveSingleItem().DefaultLiteral.ShouldBe("'pending'");
        exception.ParamName.ShouldBe("value");
    }

    private static void ShouldRender<TValue>(TValue value, string literal, DatabaseType type)
    {
        SqlCompiledSchema compiled = SqlSchema.Compile("defaults", schema => schema.Table<ValueRow<TValue>>("items", table =>
            table.Default(row => row.Value, value)));
        CompiledSchemaColumn column = compiled.Tables.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem();
        SqlCompiledSchema raw = SqlSchema.Compile("defaults", schema => schema.Table<ValueRow<TValue>>("items", table =>
            table.DefaultSql(row => row.Value, literal)));
        column.DefaultLiteral.ShouldBe(literal);
        column.Type.ShouldBe(type);
        SqlCompiledSchemaSerializer.Deserialize(compiled.CanonicalDocument).Hash.ShouldBe(compiled.Hash);
        raw.CanonicalDocument.ShouldBe(compiled.CanonicalDocument);
        raw.Hash.ShouldBe(compiled.Hash);
    }

    private sealed record ValueRow<TValue>(TValue Value);
    private enum SignedByteStatus : sbyte { Value = sbyte.MinValue }
    private enum ByteStatus : byte { Value = byte.MaxValue }
    private enum ShortStatus : short { Value = short.MinValue }
    private enum UnsignedShortStatus : ushort { Value = ushort.MaxValue }
    private enum Status { Pending = 7 }
    private enum UnsignedStatus : uint { Value = uint.MaxValue }
    private enum LongStatus : long { Value = long.MinValue }
    private enum UnsignedLongStatus : ulong { Value = ulong.MaxValue }
}
