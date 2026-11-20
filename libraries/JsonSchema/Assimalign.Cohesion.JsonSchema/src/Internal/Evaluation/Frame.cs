using System.Diagnostics;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// One schema applied to one instance location during an evaluation.
/// </summary>
/// <remarks>
/// <see cref="EvaluationContext"/> keeps one frame per depth and reuses it for every schema applied
/// at that depth, so <see cref="Enter(SchemaNode, JsonElement, bool)"/> resets all of its state.
/// </remarks>
[DebuggerDisplay("{Node.Pointer,nq} @ {InstanceLocation,nq}")]
internal sealed class Frame
{
    /// <summary>The outcome of an <c>if</c> keyword, which its <c>then</c> and <c>else</c> siblings read.</summary>
    public enum Condition
    {
        /// <summary>The schema has no <c>if</c>, or it has not been evaluated.</summary>
        None,

        /// <summary>The instance is valid against the <c>if</c> subschema: <c>then</c> applies.</summary>
        Passed,

        /// <summary>The instance is invalid against the <c>if</c> subschema: <c>else</c> applies.</summary>
        Failed,
    }

    public SchemaNode Node { get; private set; } = null!;

    public JsonElement Instance { get; private set; }

    /// <summary>
    /// Gets a value indicating whether keywords record what they evaluate in this frame: an
    /// <c>unevaluated*</c> keyword in this schema, or in a schema that applies it in place, reads it.
    /// </summary>
    public bool Tracks { get; private set; }

    /// <summary>Gets the positions of the object members keywords evaluated.</summary>
    public EvaluatedSet Properties { get; } = new();

    /// <summary>Gets the positions of the array items keywords evaluated.</summary>
    public EvaluatedSet Items { get; } = new();

    public Condition Conditional { get; set; }

    /// <summary>Gets a value indicating whether the frame moved to a member or item of its parent's instance.</summary>
    public bool MovesInstance { get; private set; }

    /// <summary>Gets the member the frame moved to, when it moved to a member.</summary>
    public JsonProperty? Member { get; private set; }

    /// <summary>Gets the index of the item the frame moved to, or -1.</summary>
    public int ItemIndex { get; private set; }

    /// <summary>Gets a value indicating whether entering the frame pushed its resource onto the dynamic scope.</summary>
    public bool PushedScope { get; set; }

    /// <summary>Gets the size of the dynamic scope while the frame is active.</summary>
    public int ScopeCount { get; set; }

    /// <summary>Gets the evaluation path of the schema; recorded only with output.</summary>
    public string? EvaluationPath { get; set; }

    /// <summary>Gets the instance location; recorded only with output.</summary>
    public string? InstanceLocation { get; set; }

    /// <summary>Gets the schema's output unit; recorded only with output.</summary>
    public OutputUnit? Unit { get; set; }

    /// <summary>Gets the unit of the keyword being evaluated; recorded only with output.</summary>
    public OutputUnit? KeywordUnit { get; set; }

    public void Enter(SchemaNode node, JsonElement instance, bool tracks)
    {
        Node = node;
        Instance = instance;
        Tracks = tracks;
        Properties.Clear();
        Items.Clear();
        Conditional = Condition.None;
        MovesInstance = false;
        Member = null;
        ItemIndex = -1;
        PushedScope = false;
        ScopeCount = 0;
        EvaluationPath = null;
        InstanceLocation = null;
        Unit = null;
        KeywordUnit = null;
    }

    public void MoveTo(JsonProperty member)
    {
        MovesInstance = true;
        Member = member;
    }

    public void MoveTo(int index)
    {
        MovesInstance = true;
        ItemIndex = index;
    }

    /// <summary>Adds what a valid in-place subschema evaluated to this frame.</summary>
    public void Merge(Frame child)
    {
        Properties.UnionWith(child.Properties);
        Items.UnionWith(child.Items);
    }
}
