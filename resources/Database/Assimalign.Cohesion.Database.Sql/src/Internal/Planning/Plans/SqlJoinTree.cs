using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Physical children with original column slots; RIGHT swaps as LEFT, FULL preserves both.</summary>
internal sealed record SqlJoinTree(int Binding, SqlJoinTree? Left = null, SqlJoinTree? Right = null, SqlJoinStep? Step = null)
{
    internal IEnumerable<int> Leaves()
    {
        var pending = new Stack<SqlJoinTree>();
        pending.Push(this);
        while (pending.TryPop(out var node))
        {
            if (node.Step is null) { yield return node.Binding; }
            else { pending.Push(node.Right!); pending.Push(node.Left!); }
        }
    }

    internal SqlJoinTree ReplaceSteps(IReadOnlyDictionary<SqlJoinStep, SqlJoinStep> steps)
    {
        var pending = new Stack<(SqlJoinTree Node, bool Ready)>();
        var rewritten = new Dictionary<SqlJoinTree, SqlJoinTree>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        pending.Push((this, false));
        while (pending.TryPop(out var next))
        {
            var node = next.Node;
            if (node.Step is null) { rewritten.Add(node, node); }
            else if (next.Ready) { rewritten.Add(node, node with { Step = steps[node.Step], Left = rewritten[node.Left!], Right = rewritten[node.Right!] }); }
            else { pending.Push((node, true)); pending.Push((node.Right!, false)); pending.Push((node.Left!, false)); }
        }
        return rewritten[this];
    }
}
