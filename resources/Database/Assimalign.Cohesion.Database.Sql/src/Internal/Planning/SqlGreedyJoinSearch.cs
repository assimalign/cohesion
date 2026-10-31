using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Connected left-deep growth. Seed with an ordered pair, then prune all extensions
/// except component/singleton joins. No masks, random state or fixed relation cap.
/// </summary>
internal static class SqlGreedyJoinSearch
{
    internal static (int[] Order, long Pairs, int States, double Cost, double Memory) Find(double[] rows,
        Func<bool[], int, (double Work, double Memory, bool Connected)> evaluate, CancellationToken cancellationToken)
    {
        int count = rows.Length;
        var included = new bool[count];
        var order = new int[count];
        long pairs = 0;
        double bestCost = double.PositiveInfinity, bestMemory = double.PositiveInfinity;
        bool connected = false;
        // Ascending binding ordinals settle exact ties, independently of hash iteration.
        for (int first = 0; first < count; first++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            included[first] = true;
            for (int inner = 0; inner < count; inner++)
            {
                if (first == inner) { continue; }
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = evaluate(included, inner);
                pairs++;
                double cost = rows[first] + rows[inner] + candidate.Work;
                if (!Better(candidate.Connected, cost, candidate.Memory)) { continue; }
                order[0] = first; order[1] = inner;
                connected = candidate.Connected; bestCost = cost; bestMemory = candidate.Memory;
            }
            included[first] = false;
        }
        included[order[0]] = included[order[1]] = true;
        double totalCost = bestCost, totalMemory = bestMemory;
        for (int length = 2; length < count; length++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bestCost = bestMemory = double.PositiveInfinity;
            connected = false;
            for (int inner = 0; inner < count; inner++)
            {
                if (included[inner]) { continue; }
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = evaluate(included, inner);
                pairs++;
                double cost = totalCost + rows[inner] + candidate.Work;
                double memory = totalMemory + candidate.Memory;
                if (!Better(candidate.Connected, cost, memory)) { continue; }
                order[length] = inner;
                connected = candidate.Connected; bestCost = cost; bestMemory = memory;
            }
            included[order[length]] = true;
            totalCost = bestCost; totalMemory = bestMemory;
        }
        return (order, pairs, checked(2 * count - 1), totalCost, totalMemory);

        bool Better(bool crossing, double cost, double memory)
            => crossing != connected ? crossing : cost < bestCost || cost == bestCost && memory < bestMemory;
    }
}
