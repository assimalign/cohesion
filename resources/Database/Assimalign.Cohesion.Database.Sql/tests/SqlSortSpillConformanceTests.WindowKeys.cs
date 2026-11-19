using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Bounded window keys equal external-sort keys for every scalar family and Unicode normalization (#1789)")]
    [InlineData("binary")] [InlineData("case_insensitive")] [InlineData("case_accent_insensitive")]
    public void Encode_WindowKeys_ShouldMatchExistingCodec(string collation)
    {
        // Arrange
        object?[] values = [null, false, true, (sbyte)-1, (short)-123, int.MinValue, long.MaxValue,
            1.25f, double.NaN, double.NegativeInfinity, double.MaxValue, -12.34m, (byte)255,
            ushort.MaxValue, uint.MaxValue, ulong.MaxValue, 'x', new DateOnly(2026, 11, 14),
            new DateTime(2026,11,14), new DateTimeOffset(2026,11,14,0,0,0,TimeSpan.FromHours(-5)),
            new TimeOnly(23,59,59), TimeSpan.FromTicks(-123456), Guid.Parse("acbdef01-1234-5678-9876-123456789abc"),
            "", "A\0é\u0301\uac01\U00010400\U0001f600", new SqlCharacter("É\u0301\uac01   "), new byte[] {0,1,128,255,0}];
        foreach (bool descending in new[] { false, true })
        {
            foreach (bool nullsFirst in new[] { false, true })
            {
                var columns = values.Select(_ => new SqlTempSortKeyCodec.Column(descending,nullsFirst,Collation.FromName(collation))).ToArray();
                var left = new byte[values.Length]; var right = new byte[values.Length];
                // Act
                SqlTempSortKeyCodec.TryEncode(values, columns, left, out var expected).ShouldBeTrue();
                SqlPlanExecutor.TryEncodeWindowKeys(values, columns, right, new byte[16384], out var actual, out bool compatible).ShouldBeTrue();
                // Assert
                compatible.ShouldBeTrue(); actual.ShouldBe(expected); right.ShouldBe(left);
                foreach (object? invalid in new object?[] { "\ud800", new object() })
                {
                    SqlTempSortKeyCodec.TryEncode([invalid], columns.AsSpan(0,1), new byte[1], out _).ShouldBeFalse();
                    SqlPlanExecutor.TryEncodeWindowKeys([invalid], columns.AsSpan(0,1), new byte[1], new byte[16384], out _, out compatible).ShouldBeFalse();
                    compatible.ShouldBeFalse();
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Wide and repeated window keys stop at the encoded bound while retaining their encoding family (#1789)")]
    [InlineData("binary")] [InlineData("case_insensitive")] [InlineData("case_accent_insensitive")]
    public void Encode_WindowWideKeys_ShouldRefuseAtBound(string collation)
    {
        // Arrange
        var column = new SqlTempSortKeyCodec.Column(false,false,Collation.FromName(collation));
        object?[] values = Enumerable.Repeat<object?>(new string('\uac01',8000),128).ToArray();
        var columns = Enumerable.Repeat(column,values.Length).ToArray();
        // Act
        bool completed = SqlPlanExecutor.TryEncodeWindowKeys(values,columns,new byte[values.Length],new byte[16384],out var result,out bool compatible);
        // Assert
        completed.ShouldBeFalse(); compatible.ShouldBeTrue(); result.ShouldBeEmpty();
    }
}
