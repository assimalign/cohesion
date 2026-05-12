using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;


/// <summary>
/// 
/// </summary>
public sealed class ValidatorFactory : IValidatorFactory
{
    private readonly IDictionary<string, IValidator> validators;

    private ValidatorFactory() { }
    internal ValidatorFactory(IDictionary<string, IValidator> validators)
    {
        this.validators = validators;
    }

    /// <inheritdoc cref="IValidatorFactory.CreateValidator(string)"/>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="validatorName"/> is null or empty.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="validatorName"/> does not exist.</exception>
    IValidator IValidatorFactory.CreateValidator(string validatorName)
    {
        if (string.IsNullOrEmpty(validatorName))
        {
            throw new ArgumentNullException(nameof(validatorName), $"The parameter 'validatorName' cannot be null or empty.");
        }
        
        return validators.TryGetValue(validatorName, out var validator) ? 
            validator:
            throw new ArgumentException($"The requested validator: '{validatorName}' does not exist.");   
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IValidatorFactory Configure(Action<ValidatorFactoryBuilder> configure)
    {
        var builder = new ValidatorFactoryBuilder();

        configure.Invoke(builder);

        return new ValidatorFactory(builder.validators);
    }
}