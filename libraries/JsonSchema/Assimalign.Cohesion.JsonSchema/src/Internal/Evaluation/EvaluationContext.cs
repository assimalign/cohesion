using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The state of one evaluation: the stack of schemas being applied, the dynamic scope that
/// <c>$dynamicRef</c> and <c>$recursiveRef</c> resolve against, the positions each schema marked
/// evaluated, and, for every output format but Flag, the output tree.
/// </summary>
/// <remarks>
/// <para>
/// Frames are reused by depth, so a Flag evaluation allocates only where a keyword needs memory of its
/// own. A context belongs to one evaluation on one thread.
/// </para>
/// <para>
/// Annotations are kept for a subschema only when it is valid (2020-12 Core §7.7.1.2). Without output,
/// they matter only to <c>unevaluatedProperties</c> and <c>unevaluatedItems</c>, so positions are
/// recorded only in frames whose schema, or a schema that applies them in place, holds one.
/// </para>
/// </remarks>
[DebuggerDisplay("Depth = {Depth}, RecordsOutput = {RecordsOutput}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class EvaluationContext
{
    private readonly List<Frame> _frames = new();
    private readonly List<SchemaResource> _dynamicScope = new();
    private int _depth = -1;

    private EvaluationContext(JsonSchemaEvaluationOptions options, bool recordsOutput)
    {
        Options = options;
        RecordsOutput = recordsOutput;
    }

    public JsonSchemaEvaluationOptions Options { get; }

    /// <summary>Gets a value indicating whether output units are recorded: every output format but Flag.</summary>
    public bool RecordsOutput { get; }

    /// <summary>
    /// Gets a value indicating whether a keyword may stop at its first failure. Without output nothing
    /// reads what a failing schema would have added, because its annotations are dropped with it.
    /// </summary>
    public bool ShortCircuit => !RecordsOutput;

    /// <summary>Gets the number of schemas being applied.</summary>
    public int Depth => _depth + 1;

    /// <summary>Gets the frame of the schema being evaluated.</summary>
    public Frame Frame => _frames[_depth];

    /// <summary>
    /// Gets a value indicating whether the current schema must apply every subschema whose annotations
    /// could matter (every <c>anyOf</c> branch, every <c>contains</c> item): output is recorded, or an
    /// <c>unevaluated*</c> keyword reads what this schema marks.
    /// </summary>
    public bool CollectsAnnotations => RecordsOutput || _frames[_depth].Tracks;

    /// <summary>Gets the dynamic scope, outermost resource first.</summary>
    public IReadOnlyList<SchemaResource> DynamicScope => _dynamicScope;

    public static JsonSchemaEvaluationResult Evaluate(SchemaNode node, JsonElement instance, JsonSchemaEvaluationOptions options)
    {
        RequireInstance(instance);
        if (options.OutputFormat == JsonSchemaOutputFormat.Flag)
        {
            return JsonSchemaEvaluationResult.Flag(Run(node, instance, options));
        }

        EvaluationContext context = new(options, recordsOutput: true);
        OutputUnit root = new(node, null, string.Empty, string.Empty);
        context.EvaluateRoot(node, instance, root);
        return JsonSchemaEvaluationResult.FromUnits(root, options.OutputFormat);
    }

    public static bool IsValid(SchemaNode node, JsonElement instance, JsonSchemaEvaluationOptions options)
    {
        RequireInstance(instance);
        return Run(node, instance, options);
    }

    /// <summary>
    /// Applies a subschema to the current instance: the in-place applicators such as <c>allOf</c> and
    /// <c>then</c>. What a valid subschema evaluated counts as evaluated here unless
    /// <paramref name="keepAnnotations"/> is <see langword="false"/>, as for <c>not</c>.
    /// </summary>
    public bool EvaluateInPlace(SchemaNode subschema, JsonElement instance, bool keepAnnotations = true)
    {
        if (!RecordsOutput && subschema.BooleanValue is bool value)
        {
            return value;
        }

        Frame parent = _frames[_depth];
        Frame frame = Enter(subschema, instance, parent.Tracks || subschema.HasUnevaluated);
        if (RecordsOutput)
        {
            Record(frame, parent, parent.EvaluationPath + subschema.RelativePath, parent.InstanceLocation!);
        }

        bool valid = Exit(frame, EvaluateFrame(frame));
        if (valid && keepAnnotations && parent.Tracks)
        {
            parent.Merge(frame);
        }

        return valid;
    }

    /// <summary>
    /// Applies the target of a reference to the current instance. The evaluation path continues
    /// through the reference keyword rather than the target's location (2020-12 Core §12.4.2).
    /// </summary>
    public bool EvaluateReference(SchemaKeyword keyword, SchemaNode target, JsonElement instance)
    {
        Frame parent = _frames[_depth];
        ThrowIfCycle(keyword, target);
        if (!RecordsOutput && target.BooleanValue is bool value)
        {
            return value;
        }

        Frame frame = Enter(target, instance, parent.Tracks || target.HasUnevaluated);
        if (RecordsOutput)
        {
            Record(frame, parent, parent.KeywordUnit!.KeywordLocation, parent.InstanceLocation!);
        }

        bool valid = Exit(frame, EvaluateFrame(frame));
        if (valid && parent.Tracks)
        {
            parent.Merge(frame);
        }

        return valid;
    }

    /// <summary>Applies a subschema to a member of the current object instance.</summary>
    public bool EvaluateProperty(SchemaNode subschema, JsonProperty member)
    {
        if (!RecordsOutput && subschema.BooleanValue is bool value)
        {
            return value;
        }

        Frame parent = _frames[_depth];
        Frame frame = Enter(subschema, member.Value, subschema.HasUnevaluated);
        frame.MoveTo(member);
        if (RecordsOutput)
        {
            Record(frame, parent, parent.EvaluationPath + subschema.RelativePath, parent.InstanceLocation + "/" + JsonPointer.EscapeToken(member.Name));
        }

        return Exit(frame, EvaluateFrame(frame));
    }

    /// <summary>Applies a subschema to an item of the current array instance.</summary>
    public bool EvaluateItem(SchemaNode subschema, JsonElement item, int index)
    {
        if (!RecordsOutput && subschema.BooleanValue is bool value)
        {
            return value;
        }

        Frame parent = _frames[_depth];
        Frame frame = Enter(subschema, item, subschema.HasUnevaluated);
        frame.MoveTo(index);
        if (RecordsOutput)
        {
            Record(frame, parent, parent.EvaluationPath + subschema.RelativePath, parent.InstanceLocation + "/" + index.ToString(CultureInfo.InvariantCulture));
        }

        return Exit(frame, EvaluateFrame(frame));
    }

    /// <summary>
    /// Applies a subschema to a value derived from the current instance rather than part of it: a
    /// member name for <c>propertyNames</c>, reported at that member's location, or decoded content for
    /// <c>contentSchema</c>, reported at the current location. Nothing it evaluates counts here, and its
    /// annotations describe the derived value, so the output drops them.
    /// </summary>
    public bool EvaluateDerived(SchemaNode subschema, JsonElement value, JsonProperty? member)
    {
        if (!RecordsOutput && subschema.BooleanValue is bool boolean)
        {
            return boolean;
        }

        Frame parent = _frames[_depth];
        Frame frame = Enter(subschema, value, subschema.HasUnevaluated);
        string? instanceLocation = parent.InstanceLocation;
        if (member is JsonProperty property)
        {
            frame.MoveTo(property);
            if (RecordsOutput)
            {
                instanceLocation = instanceLocation + "/" + JsonPointer.EscapeToken(property.Name);
            }
        }

        if (RecordsOutput)
        {
            Record(frame, parent, parent.EvaluationPath + subschema.RelativePath, instanceLocation!);
            frame.Unit!.DiscardsAnnotations = true;
        }

        return Exit(frame, EvaluateFrame(frame));
    }

    /// <summary>Records that the current keyword evaluated the member at a position of the object instance.</summary>
    public void MarkProperty(int position)
    {
        Frame frame = _frames[_depth];
        if (frame.Tracks)
        {
            frame.Properties.Add(position);
        }
    }

    /// <summary>Records that the current keyword evaluated an item of the array instance.</summary>
    public void MarkItem(int index)
    {
        Frame frame = _frames[_depth];
        if (frame.Tracks)
        {
            frame.Items.Add(index);
        }
    }

    /// <summary>Records that the current keyword evaluated the items from <paramref name="start"/> up to <paramref name="end"/>.</summary>
    public void MarkItems(int start, int end)
    {
        Frame frame = _frames[_depth];
        if (frame.Tracks)
        {
            frame.Items.AddRange(start, end);
        }
    }

    /// <summary>Reports why the current keyword failed. Without output the message is discarded.</summary>
    public void Fail(string message)
    {
        if (RecordsOutput)
        {
            _frames[_depth].KeywordUnit!.Error = message;
        }
    }

    /// <summary>Records the current keyword's annotation. Without output the value is discarded.</summary>
    public void Annotate(JsonElement annotation)
    {
        if (RecordsOutput)
        {
            _frames[_depth].KeywordUnit!.Annotation = annotation;
        }
    }

    /// <summary>
    /// Returns the outermost resource in the dynamic scope that declares a <c>$dynamicAnchor</c> of
    /// the given name (2020-12 Core §8.2.3.2).
    /// </summary>
    public SchemaNode? FindDynamicAnchor(string name)
    {
        foreach (SchemaResource resource in _dynamicScope)
        {
            if (resource.DynamicAnchors.TryGetValue(name, out SchemaNode? node))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the root of the outermost resource in the dynamic scope that declares
    /// <c>"$recursiveAnchor": true</c> (2019-09 Core §8.2.4.2.2).
    /// </summary>
    public SchemaNode? FindRecursiveAnchor()
    {
        foreach (SchemaResource resource in _dynamicScope)
        {
            if (resource.RecursiveAnchor)
            {
                return resource.Root;
            }
        }

        return null;
    }

    /// <summary>Returns the location of the current instance within the root instance.</summary>
    public JsonPointer GetInstanceLocation()
    {
        if (_frames[_depth].InstanceLocation is string location)
        {
            return JsonPointer.CreateUnchecked(location);
        }

        JsonPointer pointer = JsonPointer.Root;
        for (int depth = 0; depth <= _depth; depth++)
        {
            Frame frame = _frames[depth];
            if (frame.Member is JsonProperty member)
            {
                pointer = pointer.Append(member.Name);
            }
            else if (frame.ItemIndex >= 0)
            {
                pointer = pointer.Append(frame.ItemIndex);
            }
        }

        return pointer;
    }

    private static bool Run(SchemaNode node, JsonElement instance, JsonSchemaEvaluationOptions options)
    {
        if (node.BooleanValue is bool value)
        {
            return value;
        }

        return new EvaluationContext(options, recordsOutput: false).EvaluateRoot(node, instance, null);
    }

    private static void RequireInstance(JsonElement instance)
    {
        if (instance.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(ErrorMessages.InstanceUndefined, nameof(instance));
        }
    }

    private static void Record(Frame frame, Frame parent, string evaluationPath, string instanceLocation)
    {
        frame.EvaluationPath = evaluationPath;
        frame.InstanceLocation = instanceLocation;
        frame.Unit = new OutputUnit(frame.Node, null, evaluationPath, instanceLocation);
        parent.KeywordUnit!.Add(frame.Unit);
    }

    private bool EvaluateRoot(SchemaNode node, JsonElement instance, OutputUnit? unit)
    {
        Frame frame = new();
        _frames.Add(frame);
        frame.Enter(node, instance, node.HasUnevaluated);
        _dynamicScope.Add(node.Resource);
        frame.PushedScope = true;
        frame.ScopeCount = 1;
        if (unit is not null)
        {
            frame.EvaluationPath = string.Empty;
            frame.InstanceLocation = string.Empty;
            frame.Unit = unit;
        }

        _depth = 0;
        return EvaluateFrame(frame);
    }

    private Frame Enter(SchemaNode node, JsonElement instance, bool tracks)
    {
        int depth = _depth + 1;
        if (depth >= Options.MaxEvaluationDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw JsonSchemaProcessingException.EvaluationDepthExceeded(Options.MaxEvaluationDepth);
        }

        Frame frame;
        if (depth < _frames.Count)
        {
            frame = _frames[depth];
        }
        else
        {
            frame = new Frame();
            _frames.Add(frame);
        }

        frame.Enter(node, instance, tracks);
        if (!ReferenceEquals(_dynamicScope[^1], node.Resource))
        {
            _dynamicScope.Add(node.Resource);
            frame.PushedScope = true;
        }

        frame.ScopeCount = _dynamicScope.Count;
        _depth = depth;
        return frame;
    }

    private bool Exit(Frame frame, bool valid)
    {
        if (frame.PushedScope)
        {
            _dynamicScope.RemoveAt(_dynamicScope.Count - 1);
        }

        _depth--;
        return valid;
    }

    private bool EvaluateFrame(Frame frame)
    {
        SchemaNode node = frame.Node;
        if (node.BooleanValue is bool value)
        {
            if (!value && frame.Unit is OutputUnit falseUnit)
            {
                falseUnit.Valid = false;
                falseUnit.Error = Resources.FalseSchemaFailed;
            }

            return value;
        }

        JsonElement instance = frame.Instance;
        if (!RecordsOutput)
        {
            foreach (SchemaKeyword keyword in node.AssertingKeywords)
            {
                if (!keyword.Evaluate(this, instance))
                {
                    return false;
                }
            }

            return true;
        }

        OutputUnit unit = frame.Unit!;
        bool valid = true;
        foreach (SchemaKeyword keyword in node.Keywords)
        {
            OutputUnit keywordUnit = new(node, keyword, frame.EvaluationPath + keyword.PathSegment, frame.InstanceLocation!);
            unit.Add(keywordUnit);
            frame.KeywordUnit = keywordUnit;
            if (!keyword.Evaluate(this, instance))
            {
                keywordUnit.Valid = false;
                valid = false;
            }
        }

        frame.KeywordUnit = null;
        unit.Valid = valid;
        return valid;
    }

    /// <summary>
    /// Refuses a reference that re-enters a schema already being applied to the same instance location
    /// with the same dynamic scope: evaluation is deterministic, so it would recurse forever.
    /// </summary>
    private void ThrowIfCycle(SchemaKeyword keyword, SchemaNode target)
    {
        int scope = _dynamicScope.Count + (ReferenceEquals(_dynamicScope[^1], target.Resource) ? 0 : 1);
        for (int depth = _depth; depth >= 0; depth--)
        {
            Frame frame = _frames[depth];
            if (ReferenceEquals(frame.Node, target) && frame.ScopeCount == scope)
            {
                SchemaNode owner = keyword.Owner;
                throw JsonSchemaProcessingException.ReferenceCycle(owner.Document.FormatLocation(owner.Pointer + keyword.PathSegment));
            }

            if (frame.MovesInstance)
            {
                break;
            }
        }
    }

    private sealed class DebugView
    {
        private readonly EvaluationContext _context;

        public DebugView(EvaluationContext context)
        {
            _context = context;
        }

        public JsonSchemaEvaluationOptions Options => _context.Options;

        public bool RecordsOutput => _context.RecordsOutput;

        public int Depth => _context.Depth;

        public IReadOnlyList<SchemaResource> DynamicScope => _context.DynamicScope;
    }
}
