using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Verifies exact mixed numeric order and the equality classes used by grouping hashes.</summary>
public sealed class SqlNumericOrderTests
{
    /// <summary>Mixed comparisons retain small binary differences and exact large integer boundaries.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - comparison: mixed numeric boundaries retain represented values")]
    public void Compare_MixedNumericBoundaries_ShouldNotRoundOperands()
    {
        var pairs = new (object Left, object Right)[]
        {
            (0.1m, 0.1d),
            (1m, Math.BitIncrement(1d)),
            (9007199254740991L, 9007199254740992d),
            (-9007199254740992d, -9007199254740991L),
            (0m, double.Epsilon),
            (-double.Epsilon, 0m),
            (0.1m, 0.1f),
        };

        pairs.Select(pair => Math.Sign(SqlExpressionEvaluator.Compare(pair.Left, pair.Right)))
            .ToArray().ShouldBe(Enumerable.Repeat(-1, pairs.Length).ToArray());
    }

    /// <summary>Every pair in one mixed dataset has the same strict order in either operand direction.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - comparison: mixed numeric ordering is antisymmetric and transitive")]
    public void Compare_MixedNumericDataset_ShouldHaveOneTotalOrder()
    {
        object[] ordered =
        [
            double.NaN,
            double.NegativeInfinity,
            -double.MaxValue,
            -1e100d,
            decimal.MinValue,
            -9007199254740993L,
            -9007199254740992d,
            -1m,
            -double.Epsilon,
            0m,
            double.Epsilon,
            0.1m,
            0.1d,
            1m,
            Math.BitIncrement(1d),
            9007199254740992d,
            9007199254740993L,
            decimal.MaxValue,
            1e100d,
            double.MaxValue,
            double.PositiveInfinity,
        ];
        var comparisons = new List<int>();
        var expected = new List<int>();
        for (int left = 0; left < ordered.Length; left++)
        {
            for (int right = 0; right < ordered.Length; right++)
            {
                comparisons.Add(Math.Sign(SqlExpressionEvaluator.Compare(ordered[left], ordered[right])));
                expected.Add(Math.Sign(left.CompareTo(right)));
            }
        }

        comparisons.ShouldBe(expected);
    }

    /// <summary>Equal values hash alike across decimal scales, numeric types, NaN payloads and signed zeros.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - comparison: grouping hashes preserve exact numeric equality")]
    public void Compare_NumericEqualityClasses_ShouldHaveConsistentHashes()
    {
        decimal scaledHalf = decimal.Parse("1.50000000000000000000000", CultureInfo.InvariantCulture);
        decimal scaledThree = decimal.Parse("3.00000000000000000000000", CultureInfo.InvariantCulture);
        var equalPairs = new (object Left, object Right)[]
        {
            (1.5m, scaledHalf),
            (scaledHalf, 1.5d),
            (3L, scaledThree),
            (0.5f, 0.5m),
            (1, 1d),
            (-0d, 0m),
            (0f, -0d),
            (double.NaN, BitConverter.UInt64BitsToDouble(0x7ff8000000000001UL)),
            (float.NaN, double.NaN),
            (float.PositiveInfinity, double.PositiveInfinity),
            (float.NegativeInfinity, double.NegativeInfinity),
        };
        var equalityClasses = equalPairs.Select(pair =>
            SqlExpressionEvaluator.Compare(pair.Left, pair.Right) == 0
            && SqlValueComparer.GetHashCode(pair.Left, Collation.Binary)
                == SqlValueComparer.GetHashCode(pair.Right, Collation.Binary)).ToList();
        // An exact decimal boundary and its rounded binary approximation must
        // remain different classes even when a hash projection could collide.
        equalityClasses.Add(SqlExpressionEvaluator.Compare(0.1m, 0.1d) != 0);

        equalityClasses.ShouldBe(Enumerable.Repeat(true, equalPairs.Length + 1));
    }
}
