using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: bound alternatives produce their resolved type before output normalization (#1788)")]
    [InlineData("CASE WHEN TRUE THEN +1 ELSE 2 END", DatabaseType.Int32, 1)]
    [InlineData("CASE WHEN TRUE THEN -1 ELSE 2 END", DatabaseType.Int32, -1)]
    [InlineData("CASE WHEN TRUE THEN 1 ELSE 2.5 END", DatabaseType.Decimal, 1)]
    [InlineData("CASE WHEN FALSE THEN 1 ELSE 2.5 END", DatabaseType.Decimal, 2.5)]
    [InlineData("COALESCE(1::SMALLINT, 2)", DatabaseType.Int32, 1)]
    [InlineData("COALESCE(NULL::BIGINT, 2)", DatabaseType.Int64, 2)]
    [InlineData("COALESCE(16777217, NULL::REAL)", DatabaseType.Float32, 16777216)]
    [InlineData("COALESCE(CASE WHEN TRUE THEN 16777217 ELSE NULL::REAL END, 0::DOUBLE PRECISION)", DatabaseType.Float64, 16777216)]
    [InlineData("COALESCE(NULL::INT, '3')", DatabaseType.Int32, 3)]
    public void Bind_NumericAlternatives_ShouldProduceResolvedRuntimeType(string expression, DatabaseType type, double expected)
    {
        // Arrange: inspect the bound node directly, without a projected-row normalizer.
        var syntax = ((SqlSelectExpression)SqlQueryRequest.FromSql("SELECT " + expression).Statement.SqlExpression).Columns.Single().Expression;
        var evaluator = new SqlExpressionEvaluator([], null);

        // Act
        object? value = evaluator.Evaluate(evaluator.Bind(syntax), []);

        // Assert
        evaluator.StaticTypeOf(syntax).ShouldBe(type);
        SqlValue.FromObject(value).Type.ShouldBe(type);
        Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }
}
