using System;
using System.Numerics;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Exact subset DP. Cardinalities depend on a set, not its chosen tree.</summary>
internal static class SqlJoinSearch
{
    internal const int ExactRelationLimit = 8;

    internal static (SqlJoinTree Tree, int States, int Partitions, double Cost) Find(int count,
        Func<int, double> rows, Func<int, int, (SqlJoinStep Step, double Work)> join,
        CancellationToken cancellationToken, Func<SqlJoinTree, double>? retainedBytes = null)
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
                continue;
            }
            double best = double.PositiveInfinity;
            double bestMemory = double.PositiveInfinity;
            ulong bestOrder = ulong.MaxValue;
            for (int left = (mask - 1) & mask; left != 0; left = (left - 1) & mask)
            {
                int right = mask ^ left;
                var candidate = join(left, right);
                partitions++;
                double cost = costs[left] + costs[right] + candidate.Work;
                if (cost > best) { continue; }
                var tree = new SqlJoinTree(-1, trees[left], trees[right], candidate.Step);
                double memory = retainedBytes?.Invoke(tree) ?? 0;
                ulong order = (orders[left] << (4 * BitOperations.PopCount((uint)right))) | orders[right];
                // Retained builds precede ordinal ties; all flat builds coexist until disposal.
                if (cost == best && (memory > bestMemory || memory == bestMemory && order >= bestOrder)) { continue; }
                best = cost;
                bestMemory = memory;
                bestOrder = order;
                trees[mask] = tree;
            }
            costs[mask] = best;
            orders[mask] = bestOrder;
        }
        return (trees[^1], size - 1, partitions, costs[^1]);
    }
}

/// <summary>Search evidence exists only on searched plans; ordinary plan sizes stay unchanged.</summary>
internal sealed record SqlSearchedJoinPlan : SqlJoinPlan
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
