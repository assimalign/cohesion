using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

using Assimalign.Cohesion.ObjectValidation.Internal;

/// <summary>
/// A fluent builder for creating a <see cref="IValidator"/>.
/// </summary>
public sealed class ValidatorBuilder
{
    public ValidatorBuilder()
    {
        this.Options = new ValidationOptions();
        this.Profiles = new List<IValidationProfile>();
    }

    internal ValidationOptions Options { get; }
    internal IList<IValidationProfile> Profiles { get; }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    public void AddOptions(Action<ValidationOptions> configure)
    {
        configure.Invoke(this.Options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ValidatorBuilder AddProfile(Action<IValidationProfileBuilder> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        return AddProfile(new ValidationProfileBuilderDefault(configure));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <exception cref="ArgumentNullException"></exception>
    public ValidatorBuilder AddProfile<TValidationProfileBuilder>() where TValidationProfileBuilder : IValidationProfileBuilder, new()
    {
        return AddProfile(new TValidationProfileBuilder());
    }

    /// <summary>
    /// Adds profiles that are built from <see cref="IValidationProfileBuilder"/>.
    /// </summary>
    /// <param name="builder"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public ValidatorBuilder AddProfile(IValidationProfileBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        foreach (var profile in builder.Build())
        {
            AddProfile(profile);
        }

        return this;
    }

    /// <summary>
    /// Adds a <see cref="IValidationProfile"/> to the collection of profiles 
    /// which will be registered under an instance of <see cref="IValidator"/>.
    /// </summary>
    /// <param name="profile">The <see cref="IValidationProfile"/> to be registered.</param>
    /// <exception cref="ArgumentNullException">Thrown when the <paramref name="profile"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="IValidationProfile"/> has already been registered with the validator.</exception>
    public void AddProfile(IValidationProfile profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }
        if (this.Profiles.Any(x => x.ValidationType == profile.ValidationType))
        {
            throw new InvalidOperationException($"A Validation Profile for type: {profile.GetType().Name} has already been registered.");
        }
        var descriptor = new ValidationRuleDescriptor()
        {
            ValidationItems = profile.ValidationItems
        };

        profile.Configure(descriptor);

        this.Profiles.Add(profile);
    }

    /// <summary>
    /// Adds a <see cref="IValidationProfile{T}"/> to the collection of profiles 
    /// which will be registered under an instance of <see cref="IValidator"/>.
    /// </summary>
    /// <typeparam name="T">The type in which the validation profile is scoped to.</typeparam>
    /// <param name="profile">The <see cref="IValidationProfile{T}"/> to be registered.</param>
    /// <exception cref="ArgumentNullException">Thrown when the <paramref name="profile"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="IValidationProfile"/> has already been registered with the validator.</exception>
    public void AddProfile<T>(IValidationProfile<T> profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }
        if (this.Profiles.Any(x => x.ValidationType == profile.ValidationType))
        {
            throw new InvalidOperationException($"A Validation Profile for type: {profile.GetType().Name} has already been registered.");
        }
        var descriptor = new ValidationRuleDescriptor<T>()
        {
            ValidationItems = profile.ValidationItems
        };

        profile.Configure(descriptor);

        this.Profiles.Add(profile);
    }
}
