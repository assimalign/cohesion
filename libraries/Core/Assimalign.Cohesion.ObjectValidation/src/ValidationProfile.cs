using System;

namespace Assimalign.Cohesion.ObjectValidation;

using Assimalign.Cohesion.ObjectValidation.Internal;

/// <summary>
/// A validation profile is used to describe the rules of <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class ValidationProfile<T> : IValidationProfile<T>
{
    private readonly Type validationType;
    private readonly IValidationItemQueue validationItems;

    /// <summary>
    /// 
    /// </summary>
    public ValidationProfile()
    {
        this.validationType = typeof(T);
        this.validationItems = new ValidationItemQueue();
    }

    /// <summary>
    /// A collection validation rules to apply to the instance of <typeparamref name="T"/>
    /// for a given context.
    /// </summary>
    public IValidationItemQueue ValidationItems => this.validationItems;

    /// <summary>
    /// The type of <typeparamref name="T"/> being validated.
    /// </summary>
    public Type ValidationType => this.validationType;

    /// <summary>
    /// 
    /// </summary>
    void IValidationProfile.Configure(IValidationRuleDescriptor descriptor)
    {
        if (descriptor is ValidationRuleDescriptor<T> td)
        {
            this.Configure(td);
        }
    }

    /// <summary>
    /// Configures the validation rules to be applied on the type <typeparamref name="T"/>
    /// </summary>
    /// <param name="descriptor"></param>
    public abstract void Configure(IValidationRuleDescriptor<T> descriptor);
}


/// <summary>
/// 
/// </summary>
public abstract class ValidationProfile : IValidationProfile
{
    private readonly Type validationType;
    private readonly IValidationItemQueue validationItems;

    /// <summary>
    /// 
    /// </summary>
    public ValidationProfile(Type type)
    {
        this.validationType = type;
        this.validationItems = new ValidationItemQueue();
    }

    /// <summary>
    /// 
    /// </summary>
    public Type ValidationType => this.validationType;

    /// <summary>
    /// 
    /// </summary>
    public IValidationItemQueue ValidationItems => this.validationItems;

    /// <summary>
    /// 
    /// </summary>
    public abstract void Configure(IValidationRuleDescriptor descriptor);
}