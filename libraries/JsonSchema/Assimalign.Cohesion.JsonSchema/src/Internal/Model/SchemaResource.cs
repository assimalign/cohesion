using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// A schema resource (2020-12 Core §4.3.5): a schema object with its own base URI, the anchors
/// declared within it, and the dialect it is processed with.
/// </summary>
/// <remarks>
/// Resources are built while their document compiles and are read-only once it is published.
/// </remarks>
[DebuggerDisplay("{BaseText,nq}")]
internal sealed class SchemaResource
{
    public SchemaResource(JsonSchemaDocument document, UriReference baseUri, bool isSynthetic, JsonSchemaDialect dialect, string documentPointer)
    {
        Document = document;
        Base = baseUri;
        BaseText = baseUri.ToNormalizedString(includeFragment: false);
        IsSynthetic = isSynthetic;
        Dialect = dialect;
        DocumentPointer = documentPointer;
        Uri = isSynthetic ? null : (Uri.TryCreate(BaseText, UriKind.Absolute, out Uri? uri) ? uri : null);
    }

    public JsonSchemaDocument Document { get; }

    public UriReference Base { get; }

    /// <summary>Gets the normalized base URI, without a fragment: the key the registry uses.</summary>
    public string BaseText { get; }

    /// <summary>Gets a value indicating whether the base URI was generated for an unidentified document.</summary>
    public bool IsSynthetic { get; }

    public JsonSchemaDialect Dialect { get; }

    /// <summary>Gets the JSON Pointer of the resource's root within its document.</summary>
    public string DocumentPointer { get; }

    public Uri? Uri { get; }

    public SchemaNode Root { get; set; } = null!;

    /// <summary>Gets the plain-name fragments: <c>$anchor</c>, <c>$dynamicAnchor</c>, and fragment-only <c>$id</c> of earlier drafts.</summary>
    public Dictionary<string, SchemaNode> Anchors { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the anchors declared with <c>$dynamicAnchor</c>.</summary>
    public Dictionary<string, SchemaNode> DynamicAnchors { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets a value indicating whether the root declares <c>"$recursiveAnchor": true</c>.</summary>
    public bool RecursiveAnchor { get; set; }
}
