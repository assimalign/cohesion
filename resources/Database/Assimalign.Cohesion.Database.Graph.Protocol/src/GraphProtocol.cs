using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Protocol;

/// <summary>The graph endpoint's immutable protocol family.</summary>
public static class GraphProtocol
{
    /// <summary>Gets the capability enabling composite parameters and result rows.</summary>
    public static ProtocolCapability CompositeValues { get; } = new("graph.composite-values");

    /// <summary>Gets the graph family, including the unchanged catalog exchange and path results.</summary>
    public static ProtocolMessageFamily Family { get; } = new("Graph", [ProtocolCapability.Diagnostics, ProtocolCapability.TransactionStatus, CompositeValues], null, 5, 6, 7, 8, 9, 64, 65, 66);
}
