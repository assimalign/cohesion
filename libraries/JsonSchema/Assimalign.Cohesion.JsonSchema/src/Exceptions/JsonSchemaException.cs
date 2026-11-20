using System;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// The root of every exception the JSON Schema library throws when a schema cannot be loaded,
/// resolved or evaluated.
/// </summary>
/// <remarks>
/// An instance that fails validation is a result, not an exception. This exception reports schemas
/// the library cannot process, and <see cref="Code"/> says why. Consumers may derive their own
/// exception types from this root.
/// </remarks>
public abstract class JsonSchemaException : Exception
{
    /// <summary>
    /// Initializes the exception.
    /// </summary>
    /// <param name="code">The reason the schema could not be processed.</param>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="location">
    /// The schema location the failure concerns, as an absolute URI with a JSON Pointer fragment
    /// where the schema has one, or <see langword="null"/> when no single location applies.
    /// </param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    protected JsonSchemaException(JsonSchemaErrorCode code, string message, string? location = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Location = location;
    }

    /// <summary>
    /// Gets the reason the schema could not be processed.
    /// </summary>
    public JsonSchemaErrorCode Code { get; }

    /// <summary>
    /// Gets the schema location the failure concerns, or <see langword="null"/> when no single
    /// location applies.
    /// </summary>
    public string? Location { get; }
}
