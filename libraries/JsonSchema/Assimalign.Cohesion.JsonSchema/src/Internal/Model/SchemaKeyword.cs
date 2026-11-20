using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The compiled evaluator of one keyword occurrence.
/// </summary>
[DebuggerDisplay("{Name,nq} in {Owner.Pointer,nq}")]
internal abstract class SchemaKeyword
{
    protected SchemaKeyword(SchemaNode owner, string name, JsonElement value)
    {
        Owner = owner;
        Name = name;
        Value = value;
        PathSegment = "/" + JsonPointer.EscapeToken(name);
    }

    /// <summary>Gets the schema object that holds the keyword.</summary>
    public SchemaNode Owner { get; }

    public string Name { get; }

    public JsonElement Value { get; }

    /// <summary>Gets the keyword's name as a JSON Pointer segment, such as <c>/properties</c>.</summary>
    public string PathSegment { get; }

    /// <summary>
    /// Gets the keyword's place in its schema's evaluation order. Cheap assertions run first so a
    /// failing instance stops early; <c>unevaluated*</c> runs after every keyword whose annotations
    /// it reads.
    /// </summary>
    public virtual int Priority => KeywordPriority.Assertion;

    /// <summary>Gets a value indicating whether the keyword only annotates and can never fail.</summary>
    public virtual bool IsAnnotationOnly => false;

    /// <summary>Lets a keyword find the siblings it reads, once all of them are compiled.</summary>
    public virtual void Link(List<SchemaKeyword> siblings)
    {
    }

    /// <summary>
    /// Evaluates the keyword against an instance. Returns <see langword="false"/> when the keyword
    /// fails; a failing keyword reports its error through <see cref="EvaluationContext.Fail(string)"/>
    /// when the context records output.
    /// </summary>
    public abstract bool Evaluate(EvaluationContext context, JsonElement instance);
}
