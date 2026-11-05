using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Execution.Tests;

public sealed class DatabasePlanRowAccessorTests
{
    [Fact(DisplayName = "Cohesion Test [Execution] - Plan rows preserve typed conversions and NULL failures")]
    public async Task Accessors_CompatibleValues_ShouldRetainConversionsAndErrors()
    {
        // Arrange
        await using var result = DatabasePlanDescription.Create([new DatabasePlanNode
        { NodeId = 42, Operator = "42", EstimatedRows = long.MaxValue }]);

        // Act / Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.FieldCount.ShouldBe(8);
            row.GetInt32(0).ShouldBe(42);
            row.GetInt64(0).ShouldBe(42);
            row.GetDouble(0).ShouldBe(42);
            row.GetBoolean(0).ShouldBeTrue();
            row.GetString(0).ShouldBe("42");
            row.GetValue(0).ShouldBe(42);
            Encoding.UTF8.GetString(row.GetBytes(0).Span).ShouldBe("42");
            row.GetInt32(2).ShouldBe(42);
            row.IsNull(1).ShouldBeTrue();
            row.GetValue(1).ShouldBeNull();
            row.GetString(1).ShouldBeNull();
            row.GetBytes(1).IsEmpty.ShouldBeTrue();
            Should.Throw<InvalidOperationException>(() => row.GetInt32(1));
            Should.Throw<InvalidOperationException>(() => row.GetInt64(1));
            Should.Throw<InvalidOperationException>(() => row.GetDouble(1));
            Should.Throw<InvalidOperationException>(() => row.GetBoolean(1));
            Should.Throw<OverflowException>(() => row.GetInt32(6));
            Should.Throw<FormatException>(() => row.GetBoolean(2));
            count++;
        }
        count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Execution] - Plan rows use the common ordinal guard")]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Accessors_InvalidOrdinal_ShouldUseBaseGuard(int ordinal)
    {
        // Arrange
        await using var result = DatabasePlanDescription.Create([new DatabasePlanNode { NodeId = 0, Operator = "Scan" }]);

        // Act / Assert
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            Action[] accessors = [() => row.IsNull(ordinal), () => row.IsMissing(ordinal),
                () => row.GetValue(ordinal), () => row.GetBytes(ordinal), () => row.GetString(ordinal),
                () => row.GetInt32(ordinal), () => row.GetInt64(ordinal),
                () => row.GetBoolean(ordinal), () => row.GetDouble(ordinal)];
            foreach (var accessor in accessors)
            {
                Should.Throw<ArgumentOutOfRangeException>(accessor).ParamName.ShouldBe("ordinal");
            }
        }
    }
}
