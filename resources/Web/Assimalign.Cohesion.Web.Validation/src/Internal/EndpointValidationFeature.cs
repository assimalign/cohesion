using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.ObjectValidation;

namespace Assimalign.Cohesion.Web.Validation.Internal;

/// <summary>
/// The application-level feature <c>AddValidation</c> registers: the default and the validators, frozen
/// when the application is composed. The host seeds it onto every exchange, where
/// <c>context.ValidateAsync</c> reads it.
/// </summary>
internal sealed class EndpointValidationFeature : IHttpFeature
{
    private readonly FrozenDictionary<Type, IValidator> _validators;

    public EndpointValidationFeature(bool enabled, IReadOnlyDictionary<Type, IValidator> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);

        Enabled = enabled;
        _validators = validators.ToFrozenDictionary();
    }

    /// <inheritdoc />
    public string Name => nameof(EndpointValidationFeature);

    /// <summary>
    /// Gets whether bound values are validated when the endpoint declares nothing.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Gets the validator registered for <paramref name="type"/>, the type a value is bound as.
    /// </summary>
    /// <param name="type">The bound type.</param>
    /// <param name="validator">The registered validator, when there is one.</param>
    /// <returns><see langword="true"/> when a validator is registered for the type.</returns>
    public bool TryGetValidator(Type type, [NotNullWhen(true)] out IValidator? validator)
        => _validators.TryGetValue(type, out validator);
}
