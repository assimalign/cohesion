using System;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// Sealed endpoint-metadata carrier that associates an <see cref="IValidator"/> with a mapped
/// endpoint.
/// </summary>
/// <remarks>
/// The typed validator-carrying <c>Map*</c> overloads attach this carrier to the route at map
/// time, and the source-generated binding thunk executes the validator against the bound model
/// before invoking the handler. It is a plain concrete carrier (no interface-per-concept), read at
/// request time through the endpoint metadata collection.
/// </remarks>
public sealed class EndpointValidationMetadata
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EndpointValidationMetadata"/> class.
    /// </summary>
    /// <param name="validator">The validator to run against the endpoint's bound model.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="validator"/> is <see langword="null"/>.</exception>
    public EndpointValidationMetadata(IValidator validator)
    {
        Validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// Gets the validator associated with the endpoint.
    /// </summary>
    public IValidator Validator { get; }
}
