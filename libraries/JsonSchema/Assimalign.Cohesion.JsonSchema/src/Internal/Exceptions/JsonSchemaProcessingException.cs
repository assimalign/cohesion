using System;
using System.Globalization;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The concrete exception the library throws. Each static member builds one failure from its
/// <c>ErrorMessages</c> entry; callers throw what it returns.
/// </summary>
internal sealed class JsonSchemaProcessingException : JsonSchemaException
{
    private JsonSchemaProcessingException(JsonSchemaErrorCode code, string message, string? location, Exception? innerException)
        : base(code, message, location, innerException)
    {
    }

    public static JsonSchemaProcessingException SchemaJsonInvalid(Exception innerException) =>
        new(JsonSchemaErrorCode.InvalidSchema, ErrorMessages.SchemaJsonInvalid, null, innerException);

    public static JsonSchemaProcessingException SchemaKindInvalid(string location) =>
        new(JsonSchemaErrorCode.InvalidSchema, Format(ErrorMessages.SchemaKindInvalid, location), location, null);

    public static JsonSchemaProcessingException KeywordValueInvalid(string keyword, string location) =>
        new(JsonSchemaErrorCode.InvalidSchema, Format(ErrorMessages.KeywordValueInvalid, keyword, location), location, null);

    public static JsonSchemaProcessingException UriReferenceInvalid(string keyword, string location) =>
        new(JsonSchemaErrorCode.InvalidUri, Format(ErrorMessages.UriReferenceInvalid, keyword, location), location, null);

    public static JsonSchemaProcessingException IdFragmentInvalid(string keyword, string location) =>
        new(JsonSchemaErrorCode.InvalidUri, Format(ErrorMessages.IdFragmentInvalid, keyword, location), location, null);

    public static JsonSchemaProcessingException AnchorInvalid(string keyword, string location) =>
        new(JsonSchemaErrorCode.InvalidSchema, Format(ErrorMessages.AnchorInvalid, keyword, location), location, null);

    public static JsonSchemaProcessingException ReferenceUnresolved(string reference, string location) =>
        new(JsonSchemaErrorCode.UnresolvedReference, Format(ErrorMessages.ReferenceUnresolved, reference, location), location, null);

    public static JsonSchemaProcessingException DialectUnknown(string metaSchema, string? location) =>
        new(JsonSchemaErrorCode.UnknownDialect, Format(ErrorMessages.DialectUnknown, metaSchema), location, null);

    public static JsonSchemaProcessingException VocabularyUnsupported(string metaSchema, string vocabulary) =>
        new(JsonSchemaErrorCode.UnsupportedVocabulary, Format(ErrorMessages.VocabularyUnsupported, metaSchema, vocabulary), metaSchema, null);

    public static JsonSchemaProcessingException ResourceDuplicate(string uri) =>
        new(JsonSchemaErrorCode.DuplicateResource, Format(ErrorMessages.ResourceDuplicate, uri), uri, null);

    public static JsonSchemaProcessingException PatternInvalid(string pattern, string location, string reason, Exception? innerException) =>
        new(JsonSchemaErrorCode.InvalidRegularExpression, Format(ErrorMessages.PatternInvalid, pattern, location, reason), location, innerException);

    public static JsonSchemaProcessingException PatternTimeout(string pattern, string location, Exception innerException) =>
        new(JsonSchemaErrorCode.RegularExpressionTimeout, Format(ErrorMessages.PatternTimeout, pattern, location), location, innerException);

    public static JsonSchemaProcessingException EvaluationDepthExceeded(int maximum) =>
        new(JsonSchemaErrorCode.EvaluationDepthExceeded, Format(ErrorMessages.EvaluationDepthExceeded, maximum), null, null);

    public static JsonSchemaProcessingException ReferenceCycle(string location) =>
        new(JsonSchemaErrorCode.EvaluationDepthExceeded, Format(ErrorMessages.ReferenceCycle, location), location, null);

    public static JsonSchemaProcessingException MetaSchemaViolation(string metaSchema, string? location) =>
        new(JsonSchemaErrorCode.MetaSchemaViolation, Format(ErrorMessages.MetaSchemaViolation, metaSchema), location, null);

    private static string Format(string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, format, arguments);
}
