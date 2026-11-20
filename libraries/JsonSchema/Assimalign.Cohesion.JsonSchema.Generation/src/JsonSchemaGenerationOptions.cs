using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Options for generating a schema document from a System.Text.Json contract with
/// <see cref="JsonSchemaGenerationExtensions.Generate(System.Text.Json.Serialization.Metadata.JsonTypeInfo, JsonSchemaGenerationOptions?, JsonSchemaDocumentOptions?)"/>.
/// </summary>
[DebuggerDisplay("{Dialect.Id,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaGenerationOptions
{
    private readonly JsonSchemaDialect _dialect = JsonSchemaDialect.Draft202012;
    private readonly Uri? _id;

    /// <summary>
    /// Gets the options every generation uses when none are given: the 2020-12 dialect, no identifier,
    /// null-oblivious values nullable, and no transform.
    /// </summary>
    public static JsonSchemaGenerationOptions Default { get; } = new();

    /// <summary>
    /// Gets the dialect the document is written in and stamped with as <c>$schema</c>:
    /// <see cref="JsonSchemaDialect.Draft202012"/> (the default), <see cref="JsonSchemaDialect.Draft201909"/>
    /// or <see cref="JsonSchemaDialect.Draft07"/>.
    /// </summary>
    /// <remarks>
    /// The dialect decides where shared schemas go (<c>$defs</c>, or <c>definitions</c> in draft-07) and,
    /// in draft-07, which ignores the siblings of <c>$ref</c>, moves each reference that has siblings into
    /// an <c>allOf</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value is not one of the three supported dialects.</exception>
    public JsonSchemaDialect Dialect
    {
        get => _dialect;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value != JsonSchemaDialect.Draft202012 && value != JsonSchemaDialect.Draft201909 && value != JsonSchemaDialect.Draft07)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DialectNotSupported, value.Id), nameof(value));
            }

            _dialect = value;
        }
    }

    /// <summary>
    /// Gets the identifier stamped on the document as <c>$id</c>, which becomes its base URI, or
    /// <see langword="null"/> for none.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not an absolute URI, or it has a fragment.</exception>
    public Uri? Id
    {
        get => _id;
        init
        {
            if (value is not null && (!value.IsAbsoluteUri || value.OriginalString.Contains('#', StringComparison.Ordinal)))
            {
                throw new ArgumentException(ErrorMessages.SchemaIdInvalid, nameof(value));
            }

            _id = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a value whose contract does not say if it may be null is treated
    /// as non-nullable: the root, a collection item or a dictionary value of a reference type. The
    /// default is <see langword="false"/>, so those values also allow <c>null</c>, which System.Text.Json
    /// writes for a null reference.
    /// </summary>
    /// <remarks>
    /// Properties are unaffected: their nullable annotations decide. Collection items carry no nullable
    /// annotation in a contract, so <see langword="true"/> also rejects <c>null</c> in a
    /// <c>List&lt;T?&gt;</c>.
    /// </remarks>
    public bool TreatNullObliviousAsNonNullable { get; init; }

    /// <summary>
    /// Gets a function that may change or replace each schema node the generator produces, or
    /// <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The function runs once for every node that describes a contract, children before their parent,
    /// before <c>$schema</c>, <c>$id</c> and the definitions are added to the root. It receives the node
    /// it may change and returns that node or another node that belongs to no other node. A node it
    /// returns is part of the document as is, and in draft-07 a <c>$ref</c> it leaves with siblings is
    /// moved into an <c>allOf</c>.
    /// </para>
    /// <para>
    /// Nodes the contract does not describe, such as the alternative that allows <c>null</c> or the
    /// <c>const</c> of a type discriminator, are not passed to it.
    /// </para>
    /// </remarks>
    public Func<IJsonSchemaGenerationContext, JsonNode, JsonNode>? TransformSchemaNode { get; init; }

    private sealed class DebugView
    {
        private readonly JsonSchemaGenerationOptions _options;

        public DebugView(JsonSchemaGenerationOptions options)
        {
            _options = options;
        }

        public JsonSchemaDialect Dialect => _options.Dialect;

        public Uri? Id => _options.Id;

        public bool TreatNullObliviousAsNonNullable => _options.TreatNullObliviousAsNonNullable;

        public Func<IJsonSchemaGenerationContext, JsonNode, JsonNode>? TransformSchemaNode => _options.TransformSchemaNode;
    }
}
