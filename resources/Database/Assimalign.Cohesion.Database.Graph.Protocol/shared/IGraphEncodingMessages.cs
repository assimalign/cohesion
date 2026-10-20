// Deviates from the repo RootNamespace rule per design decision: linked encoding logic keeps its owning namespace in both assemblies.
namespace Assimalign.Cohesion.Database.Graph.Protocol.Internal;

// Static getters defer resource lookup until a throw while retaining each assembly's
// resource ownership and the culture active at the point of failure.
internal interface IGraphEncodingMessages
{
    static abstract string CompositeLimit { get; }
    static abstract string CompositeMalformed { get; }
    static abstract string CompositeRequired { get; }
}
