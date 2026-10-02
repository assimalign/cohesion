using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ObjectValidation;

namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// Configures request validation for an application: whether bound values are validated by default, and
/// the validator for each model type.
/// </summary>
/// <remarks>
/// <para>
/// Validators are registered per model type and looked up by the type a value is bound as —
/// <c>typeof(T)</c>, never by inspecting the value — so registration and lookup involve no reflection.
/// A value whose type has no registered validator is not validated. The lookup is exact: a validator
/// registered for a base type does not validate a derived type, and the reverse.
/// </para>
/// <para>
/// <c>Assimalign.Cohesion.ObjectValidation</c> is the engine: register a configured
/// <see cref="IValidator"/> (from <c>Validator.Create</c>, or from an <see cref="IValidatorFactory"/>),
/// or a single <see cref="IValidationProfile{T}"/>, which is given a validator of its own. A validator is
/// shared by every request, so it must be safe to use concurrently; do not change a profile after it is
/// registered.
/// </para>
/// <para>
/// The <c>errors</c> map carries the failures the validator's options report. With the ObjectValidation
/// defaults, every failing member is reported, each with one failing rule's messages;
/// <see cref="ValidationOptions.ContinueThroughValidationChain"/> reports every failing rule of every
/// member, and <see cref="AddProfile{T}"/> sets it. A validator built with <see cref="ValidationMode.Stop"/>
/// reports only the first failing member. A validator built with
/// <see cref="ValidationOptions.ThrowExceptionOnFailure"/> throws a <see cref="ValidationFailureException"/>
/// that carries no errors; the request is still answered with <c>400</c>, but with an empty
/// <c>errors</c> map.
/// </para>
/// </remarks>
public sealed class EndpointValidationOptions
{
    private readonly Dictionary<Type, IValidator> _validators = new();

    /// <summary>
    /// Gets or sets whether bound values are validated by default. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// An endpoint or route group overrides the default with <c>DisableValidation()</c> or
    /// <c>RequireValidation()</c>; the most specific declaration wins. With the default off, only the
    /// endpoints that require validation are validated.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets the registered validators, keyed by the model type each validates.
    /// </summary>
    internal IReadOnlyDictionary<Type, IValidator> Validators => _validators;

    /// <summary>
    /// Registers <paramref name="validator"/> for every model type it has a profile for
    /// (each <see cref="IValidationProfile.ValidationType"/> in <see cref="IValidator.Profiles"/>).
    /// </summary>
    /// <param name="validator">The validator, typically created with <c>Validator.Create</c>.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="validator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="validator"/> has no profiles, so it validates no type; register it for a type with
    /// <see cref="AddValidator{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A validator is already registered for one of the types.</exception>
    public EndpointValidationOptions AddValidator(IValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);

        List<Type> types = new();
        foreach (IValidationProfile profile in validator.Profiles)
        {
            if (!types.Contains(profile.ValidationType))
            {
                types.Add(profile.ValidationType);
            }
        }

        if (types.Count == 0)
        {
            throw new ArgumentException(
                "The validator has no profiles, so it validates no type. Register it for a model type with AddValidator<T>.",
                nameof(validator));
        }

        foreach (Type type in types)
        {
            EnsureUnregistered(type);
        }

        foreach (Type type in types)
        {
            _validators.Add(type, validator);
        }

        return this;
    }

    /// <summary>
    /// Registers <paramref name="validator"/> for values bound as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The model type the validator validates.</typeparam>
    /// <param name="validator">The validator.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="validator"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A validator is already registered for <typeparamref name="T"/>.</exception>
    public EndpointValidationOptions AddValidator<T>(IValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        EnsureUnregistered(typeof(T));

        _validators.Add(typeof(T), validator);
        return this;
    }

    /// <summary>
    /// Registers a validator built from <paramref name="profile"/> for values bound as
    /// <typeparamref name="T"/>. The validator reports every failing rule of every member
    /// (<see cref="ValidationOptions.ContinueThroughValidationChain"/> is set).
    /// </summary>
    /// <typeparam name="T">The model type the profile validates.</typeparam>
    /// <param name="profile">The profile. It is configured once, here; do not add the same instance elsewhere.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A validator is already registered for <typeparamref name="T"/>.</exception>
    public EndpointValidationOptions AddProfile<T>(IValidationProfile<T> profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureUnregistered(typeof(T));

        IValidator validator = Validator.Create(builder =>
        {
            builder.AddOptions(options => options.ContinueThroughValidationChain = true);
            builder.AddProfile(profile);
        });

        _validators.Add(typeof(T), validator);
        return this;
    }

    private void EnsureUnregistered(Type type)
    {
        if (_validators.ContainsKey(type))
        {
            throw new InvalidOperationException($"A validator is already registered for '{type}'.");
        }
    }
}
