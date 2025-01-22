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
    /// <param name="validatorName"></param>
    /// <returns><see cref="IValidator"/></returns>
    /// <remarks>
    /// It is recommended to throw the following exceptions when implementing a custom factory.
    /// <list type="bullet">
    ///     <item>
    ///         Throw <see cref="ArgumentNullException"/> when <paramref name="validatorName"/> is null or empty.
    ///     </item>
    ///     <item>
    ///         Throw <see cref="ArgumentException"/> when <paramref name="validatorName"/> does not exist.
    ///     </item>
    /// </list>
    /// </remarks>
    IValidator CreateValidator(string validatorName);
}

