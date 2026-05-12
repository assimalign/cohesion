using System;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// A factory pattern for scoping validator's to validator names.
/// </summary>
public interface IValidatorFactory
{
    /// <summary>
    /// Creates a validator scoped to the given validator name.
    /// </summary>
    /// <param name="name"></param>
    /// <returns><see cref="IValidator"/></returns>
    IValidator CreateValidator(string name);
}

