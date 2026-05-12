using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// A fluent builder for creating a <see cref="IValidatorFactory"/>.
/// </summary>
public sealed class ValidatorFactoryBuilder
{
    internal readonly ConcurrentDictionary<string, IValidator> validators;

    internal ValidatorFactoryBuilder()
    {
        this.validators = new ConcurrentDictionary<string, IValidator>();
    }

    internal IDictionary<string, IValidator> Validators => this.validators;

    /// <summary>
    /// Builds a Validator scoped to the <paramref name="validatorName"/>.
    /// </summary>
    /// <param name="validatorName"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public ValidatorFactoryBuilder AddValidator(string validatorName, Action<ValidatorBuilder> configure)
    {
        if (string.IsNullOrEmpty(validatorName))
        {
            throw new ArgumentNullException(nameof(validatorName), $"The parameter 'validatorName' cannot be null or empty.");
        }

        this.validators.GetOrAdd(validatorName, validatorName => Validator.Create(configure));

        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="validatorName"></param>
    /// <param name="builder"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ValidatorFactoryBuilder AddValidator(string validatorName, IValidationProfileBuilder builder)
    {
        if (string.IsNullOrEmpty(validatorName))
        {
            throw new ArgumentNullException(nameof(validatorName), $"The parameter 'validatorName' cannot be null or empty.");
        }
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        return AddValidator(validatorName, validatoBuilder =>
        {
            validatoBuilder.AddProfile(builder);
        });
    }

    /// <summary>
    /// Builds the <see cref="IValidatorFactory"/> with the 
    /// configured validation profiles.
    /// </summary>
    /// <returns></returns>
    public IValidatorFactory Build()
    {
        return new ValidatorFactory(validators);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static IValidatorFactory Create(Action<ValidatorFactoryBuilder> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new ValidatorFactoryBuilder();

        configure.Invoke(builder);

        return builder.Build();
    }
}