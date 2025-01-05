using System;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// Defines the validation rules for the <see cref="IValidationProfile.ValidationType"/>.
/// </summary>
public interface IValidationProfile
{
    /// <summary>
    /// The type to be validated.
    /// </summary>
    Type ValidationType { get; }
    /// <summary>
    /// A collection of validation rules to apply 
    /// to the context being validated.
    /// </summary>
    IValidationItemQueue ValidationItems { get; }
    /// <summary>
    /// Configures the validation rules for the specified type.
    /// </summary>
    void Configure(IValidationRuleDescriptor descriptor);
}

/// <summary>
///  Configures the validation rules for the specified type.
/// </summary>
public interface IValidationProfile<T> : IValidationProfile
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="descriptor"></param>
    void Configure(IValidationRuleDescriptor<T> descriptor);
}