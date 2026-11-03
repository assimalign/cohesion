using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

public sealed partial class GraphCatalog
{
    // One database-scoped type. This identity is private to kinds 6-8 and is never a token identity.
    internal static readonly Guid GraphTypeId = new("672bf6e9-3491-4306-a153-2a0f4c0370ed");
    internal Action<string>? AfterGraphTypeMutation { get; set; }

    internal GraphType? GetGraphType(TransactionSnapshot snapshot, Action<long>? reserve = null)
        => Find(6, Guid.Empty, "CURRENT", snapshot) is null ? null : GraphType.Create(
            List(7, GraphTypeId, snapshot, reserve).Select(item => item.Record.NodeType!),
            List(8, GraphTypeId, snapshot, reserve).Select(item => item.Record.EdgeType!));

    internal GraphNodeType? GetNodeTypeForWrite(string label, TransactionSnapshot snapshot, TransactionSnapshot latest)
    {
        var previous = Find(7, GraphTypeId, label, snapshot);
        if (previous?.Reference != Find(7, GraphTypeId, label, latest)?.Reference) { throw Conflict(); }
        return previous?.Record.NodeType;
    }

    internal async ValueTask SetGraphTypeAsync(GraphType? type, TransactionContext context, CancellationToken token, Action<long>? reserve = null)
    {
        EnsureActive(context);
        var previous = List(6, null, context.Snapshot).Concat(List(7, GraphTypeId, context.Snapshot, reserve))
            .Concat(List(8, GraphTypeId, context.Snapshot, reserve)).ToArray();
        // GraphSchema owns the writer lock. Still preserve the catalog's stale-snapshot contract.
        foreach (var record in previous) { EnsureUnchanged(record.Record.Kind, record.Record.Parent, record.Record.Name, context); }
        foreach (byte kind in new byte[] { 6, 7, 8 })
        {
            if (!List(kind, kind == 6 ? null : GraphTypeId, Latest(context), reserve).Select(item => item.Reference).SequenceEqual(
                previous.Where(item => item.Record.Kind == kind).Select(item => item.Reference))) { throw Conflict(); }
        }
        await DeleteAsync(previous.Select(item => (Found?)item), context, token).ConfigureAwait(false);
        AfterGraphTypeMutation?.Invoke("tombstones");
        if (type is null) { return; }
        await SaveAsync(new CatalogRecord(graphType: GraphType.Create()), null, context, token).ConfigureAwait(false);
        AfterGraphTypeMutation?.Invoke("marker");
        foreach (var node in type.NodeTypes)
        {
            await SaveAsync(new CatalogRecord(nodeType: node), null, context, token).ConfigureAwait(false);
            AfterGraphTypeMutation?.Invoke("node:" + node.IdentifyingLabel);
        }
        foreach (var edge in type.EdgeTypes)
        {
            await SaveAsync(new CatalogRecord(edgeType: edge), null, context, token).ConfigureAwait(false);
            AfterGraphTypeMutation?.Invoke("edge:" + edge.RelationshipType);
        }
    }
}
