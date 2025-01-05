using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

using Assimalign.Cohesion.ObjectValidation.Internal;

/// <summary>
/// Am abstract class that allows for fluent building of validation profiles.
/// </summary>
public abstract class ValidationProfileBuilder : IValidationProfileBuilder
{
    private bool isBuilt;
    private IList<IValidationProfile> profiles;

    public ValidationProfileBuilder()
    {
        this.profiles = new List<IValidationProfile>();
    }


    /// <summary>
    /// An overload that is called on profile build.
    /// </summary>
    /// <param name="builder"></param>
    protected abstract void OnBuild(IValidationProfileBuilder builder);

    
    /// <inheritdoc />
    IValidationProfileBuilder IValidationProfileBuilder.CreateProfile<T>(Action<IValidationRuleDescriptor<T>> configure)
    {
        var profile = new ValidationProfileDefault<T>(configure);
        var descriptor = new ValidationRuleDescriptor<T>()
        {
            ValidationItems = profile.ValidationItems,
        };

        profile.Configure(descriptor);

        profiles.Add(profile);

        return this;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Once built the return 
    /// </remarks>
    IEnumerable<IValidationProfile> IValidationProfileBuilder.Build()
    {
        if (!isBuilt)
        {
            OnBuild(this);
            isBuilt = true;
        }
        return profiles;
    }
}
