using System;
using System.Numerics;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Exact subset DP. Cardinalities depend on a set, not its chosen tree.</summary>
internal static class SqlJoinSearch
{
    internal const int ExactRelationLimit = 8;

    // Log/exp estimates may differ in the last bits across C runtimes. A relative
    // 1e-12 cost tolerance lets memory and binding ordinals settle these near-ties.
    // Memory admission itself has no tolerance: the allowed increase is zero.
    internal static int CompareCost(double left, double right)
        => left == right ? 0 : double.IsPositiveInfinity(right) ? -1 : double.IsPositiveInfinity(left) ? 1 :
            Math.Abs(left - right) <= 1e-12 * Math.Max(Math.Abs(left), Math.Abs(right)) ? 0 : left < right ? -1 : 1;

    internal static (SqlJoinTree Tree, int States, int Partitions, double Cost) Find(int count,
        Func<int, double> rows, Func<int, int, (SqlJoinStep Step, double Work)> join, CancellationToken cancellationToken)
        => Find(count, rows, (left, right) =>
        {
            var candidate = join(left, right);
            var step = candidate.Step;
            return new SqlJoinSearchJoin(step.Binding, step.Kind, step.Condition, step.Access, step.Hash, candidate.Work);
        }, cancellationToken, null, null);

    internal static (SqlJoinTree Tree, int States, int Partitions, double Cost) Find(int count,
        Func<int, double> rows, Func<int, int, SqlJoinSearchJoin> join, CancellationToken cancellationToken,
        Func<int, int, SqlJoinSearchJoin, double>? retainedBytes,
        Action<int, int, int, SqlJoinSearchJoin>? finalized)
    {
        int size = 1 << count;
        var trees = new SqlJoinTree[size];
        var costs = new double[size];
        var orders = new ulong[size];
        int partitions = 0;
        for (int mask = 1; mask < size; mask++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BitOperations.IsPow2(mask))
            {
                int binding = BitOperations.TrailingZeroCount(mask);
                trees[mask] = new(binding);
                costs[mask] = rows(mask);
                orders[mask] = (ulong)binding;
                finalized?.Invoke(mask, 0, 0, default);
                continue;
            }
            double best = double.PositiveInfinity, bestMemory = double.NaN;
            ulong bestOrder = ulong.MaxValue;
            int bestLeft = 0, bestRight = 0;
            SqlJoinSearchJoin bestJoin = default;
            for (int left = (mask - 1) & mask; left != 0; left = (left - 1) & mask)
            {
                int right = mask ^ left;
                partitions++;
                // Work is nonnegative. Count pruned partitions to preserve the public
                // search evidence while avoiding physical path construction for them.
                if (CompareCost(costs[left] + costs[right], best) > 0) { continue; }
                var candidate = join(left, right);
                double cost = costs[left] + costs[right] + candidate.Work;
                int comparison = CompareCost(cost, best);
                if (comparison > 0) { continue; }
                double memory = double.NaN;
                ulong order = (orders[left] << (4 * BitOperations.PopCount((uint)right))) | orders[right];
                if (comparison == 0)
                {
                    memory = retainedBytes?.Invoke(left, right, candidate) ?? 0;
                    if (double.IsNaN(bestMemory)) { bestMemory = retainedBytes?.Invoke(bestLeft, bestRight, bestJoin) ?? 0; }
                    if (memory > bestMemory || memory == bestMemory && order >= bestOrder) { continue; }
                }
                best = cost;
                bestMemory = memory;
                bestOrder = order;
                bestLeft = left; bestRight = right; bestJoin = candidate;
            }
            // One tree and step per finalized subset, never per losing partition.
            trees[mask] = new(-1, trees[bestLeft], trees[bestRight], bestJoin.Step());
            costs[mask] = best;
            orders[mask] = bestOrder;
            finalized?.Invoke(mask, bestLeft, bestRight, bestJoin);
        }
        return (trees[^1], size - 1, partitions, costs[^1]);
    }
}

internal readonly record struct SqlJoinSearchJoin(int Binding, SqlJoinType Kind, SqlBoundExpression? Condition,
    SqlJoinIndexPath? Access, SqlHashJoinPath? Hash, double Work)
{
    internal SqlJoinStep Step() => new(Binding, Kind, Condition, Access) { Hash = Hash };
}

/// <summary>Search evidence exists only on searched plans; ordinary plan sizes stay unchanged.</summary>
internal record SqlSearchedJoinPlan : SqlJoinPlan
{
    internal SqlSearchedJoinPlan(SqlJoinPlan original, int states, int partitions, double cost,
        string order, bool reordered, bool sourceOnly) : base(original)
    {
        States = states;
        Partitions = partitions;
        SearchCost = cost;
        ChosenOrder = order;
        Reordered = reordered;
        SourceOnly = sourceOnly;
    }

    internal int States { get; }
    internal int Partitions { get; }
    internal double SearchCost { get; }
    internal string ChosenOrder { get; }
    internal bool Reordered { get; }
    internal bool SourceOnly { get; }
}
