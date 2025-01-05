using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public interface IValidationProfileBuilder
{
    /// <summary>
    /// Creates a profile from the delegate descriptor and returns 
    /// the current instance of the <see cref="IValidationProfileBuilder"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="descriptor"></param>
    /// <returns></returns>
    IValidationProfileBuilder CreateProfile<T>(Action<IValidationRuleDescriptor<T>> descriptor);

    /// <summary>
    /// Builds the collection of validation profiles.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IValidationProfile> Build();
}
