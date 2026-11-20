namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Identifies why a schema could not be loaded, resolved or evaluated.
/// </summary>
/// <remarks>
/// A failed validation is never an error: an instance that does not satisfy its schema produces an
/// invalid <see cref="JsonSchemaEvaluationResult"/>. These codes describe schemas the library cannot
/// process at all, carried by <see cref="JsonSchemaException.Code"/>.
/// </remarks>
public enum JsonSchemaErrorCode
{
    /// <summary>
    /// The schema is not valid JSON, is not an object or a boolean, or a known keyword has a value
    /// of the wrong shape (for example a negative <c>minLength</c>).
    /// </summary>
    InvalidSchema = 1,

    /// <summary>
    /// A URI-valued keyword (<c>$id</c>, <c>id</c>, <c>$ref</c>, <c>$schema</c> and the dynamic and
    /// recursive references) is not a valid URI reference, or an identifier carries a fragment its
    /// dialect forbids.
    /// </summary>
    InvalidUri = 2,

    /// <summary>
    /// A reference names a document, pointer or anchor that the document, its registry and the
    /// registry's resolver cannot provide.
    /// </summary>
    UnresolvedReference = 3,

    /// <summary>
    /// <c>$schema</c> names a meta-schema that is neither a built-in dialect, a registered dialect
    /// nor a document the registry can resolve.
    /// </summary>
    UnknownDialect = 4,

    /// <summary>
    /// A meta-schema's <c>$vocabulary</c> marks a vocabulary the registry does not know as
    /// required.
    /// </summary>
    UnsupportedVocabulary = 5,

    /// <summary>
    /// Two schema resources in one document or one registry claim the same absolute URI.
    /// </summary>
    DuplicateResource = 6,

    /// <summary>
    /// A <c>pattern</c> or <c>patternProperties</c> regular expression is not a supported ECMA-262
    /// regular expression.
    /// </summary>
    InvalidRegularExpression = 7,

    /// <summary>
    /// A regular expression exceeded the configured match timeout while it was evaluated.
    /// </summary>
    RegularExpressionTimeout = 8,

    /// <summary>
    /// Evaluation exceeded the configured maximum depth, or a reference re-entered itself without
    /// consuming any of the instance (an infinitely recursive schema).
    /// </summary>
    EvaluationDepthExceeded = 9,

    /// <summary>
    /// Schema validation was requested at load time and the schema does not conform to its
    /// meta-schema.
    /// </summary>
    MetaSchemaViolation = 10,
}
