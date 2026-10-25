using System.Runtime.CompilerServices;
namespace Assimalign.Cohesion.Database.Graph.Internal;

// Three arguments cover every native scalar without allocating an argument array.
[System.Diagnostics.DebuggerDisplay("GraphInlineArguments")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphInlineArguments.DebugView))]
[InlineArray(3)]
internal struct GraphInlineArguments
{
    private object? _element0;
    private sealed class DebugView { public DebugView(GraphInlineArguments value) { _ = value; } }
}
