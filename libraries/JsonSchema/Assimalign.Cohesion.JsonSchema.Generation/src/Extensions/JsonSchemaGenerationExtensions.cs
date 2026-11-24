using System;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Generates schema documents from System.Text.Json contracts.
/// </summary>
public static class JsonSchemaGenerationExtensions
{
    extension(JsonSchemaDocument)
    {
        /// <summary>
        /// Generates a schema document that describes the JSON a System.Text.Json contract reads and writes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The contract is exported with <see cref="System.Text.Json.Schema.JsonSchemaExporter"/>, which reads
        /// only contract metadata, so a source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
        /// works under NativeAOT and trimming. The export is then rewritten: a type used in more than one place
        /// or in its own definition moves into <c>$defs</c> (<c>definitions</c> in draft-07) under a name taken
        /// from the type, nullability is applied where each value is used, and the document is stamped with
        /// <c>$schema</c> and, when given, <c>$id</c>.
        /// </para>
        /// <para>
        /// A value of the contract's type, serialized with the same contract, conforms to the document. A
        /// property that has its own converter is described by the <c>true</c> schema, since only the
        /// converter knows what it writes; <see cref="JsonSchemaGenerationOptions.TransformSchemaNode"/> can
        /// describe it.
        /// </para>
        /// </remarks>
        /// <param name="typeInfo">The contract of the root type.</param>
        /// <param name="options">The generation options, or <see langword="null"/> for <see cref="JsonSchemaGenerationOptions.Default"/>.</param>
        /// <param name="documentOptions">
        /// The options the generated document is loaded with, or <see langword="null"/> for
        /// <see cref="JsonSchemaDocumentOptions.Default"/>. Their <see cref="JsonSchemaDocumentOptions.DefaultDialect"/>
        /// has no effect, since the document names its dialect.
        /// </param>
        /// <returns>The generated document.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
        /// <exception cref="NotSupportedException">
        /// The contract's options use <see cref="System.Text.Json.Serialization.ReferenceHandler.Preserve"/>, which
        /// the exporter does not support.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The contract nests deeper than its options' maximum depth, or
        /// <see cref="JsonSchemaGenerationOptions.TransformSchemaNode"/> returned <see langword="null"/>, returned a
        /// node that belongs to another node, or added a definition to the root under a name the generator uses.
        /// </exception>
        /// <exception cref="JsonSchemaException">
        /// The document does not load: a transform produced an invalid schema, the identifier is already
        /// registered in the registry of <paramref name="documentOptions"/>, or
        /// <see cref="JsonSchemaDocumentOptions.ValidateSchema"/> is set and the document does not conform to
        /// its meta-schema.
        /// </exception>
        public static JsonSchemaDocument Generate(JsonTypeInfo typeInfo, JsonSchemaGenerationOptions? options = null, JsonSchemaDocumentOptions? documentOptions = null)
        {
            ArgumentNullException.ThrowIfNull(typeInfo);
            return SchemaGenerator.Generate(typeInfo, options ?? JsonSchemaGenerationOptions.Default, documentOptions);
        }
    }
}
