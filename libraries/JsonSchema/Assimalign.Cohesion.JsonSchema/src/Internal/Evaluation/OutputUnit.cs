using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// One node of the evaluation tree recorded for the Basic, Detailed and Verbose output formats: a
/// schema applied to an instance location, or one keyword of that schema.
/// </summary>
[DebuggerDisplay("{KeywordLocation,nq} @ {InstanceLocation,nq} valid={Valid}")]
internal sealed class OutputUnit
{
    public OutputUnit(SchemaNode node, SchemaKeyword? keyword, string keywordLocation, string instanceLocation)
    {
        Node = node;
        Keyword = keyword;
        KeywordLocation = keywordLocation;
        InstanceLocation = instanceLocation;
        Valid = true;
    }

    /// <summary>Gets the schema the unit belongs to; for a keyword unit, the schema that holds the keyword.</summary>
    public SchemaNode Node { get; }

    /// <summary>Gets the keyword, or <see langword="null"/> for a schema unit.</summary>
    public SchemaKeyword? Keyword { get; }

    /// <summary>Gets the evaluation path: the JSON Pointer of the keyword or schema, through references.</summary>
    public string KeywordLocation { get; }

    public string InstanceLocation { get; }

    public bool Valid { get; set; }

    public string? Error { get; set; }

    public JsonElement? Annotation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the annotations of the unit and everything below it are
    /// dropped: a schema applied to a value derived from the instance, such as a member name, describes
    /// that value rather than the instance.
    /// </summary>
    public bool DiscardsAnnotations { get; set; }

    public List<OutputUnit>? Children { get; private set; }

    public void Add(OutputUnit child)
    {
        (Children ??= new List<OutputUnit>()).Add(child);
    }
}
