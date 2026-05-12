using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections;
using Assimalign.Cohesion.ObjectValidation.Internal;

namespace Assimalign.Cohesion.ObjectValidation;


/// <summary>
/// 
/// </summary>
public sealed class Validator : IValidator
{
    private readonly ValidationOptions options;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="profiles"></param>
    /// <param name="options"></param>
    public Validator(IEnumerable<IValidationProfile> profiles, ValidationOptions options)
    {
        this.Profiles = profiles;
        this.options = options ?? new ValidationOptions();

        var duplicates = Profiles
            .GroupBy(x => x.ValidationType)
            .Select(group => new
            {
                Type = group,
                Count = group.Count()
            })
            .FirstOrDefault(x => x.Count > 1);

        if (duplicates is not null && duplicates.Count > 1)
        {
            throw new InvalidOperationException($"A Validation Profile for type: {duplicates.Type.Key.Name} has already been registered.");
        }
    }

    /// <inheritdoc cref="IValidator.Profiles"/>
    public IEnumerable<IValidationProfile> Profiles { get; }

    /// <inheritdoc cref="IValidator.Validate{T}(T)"/>
    public ValidationResult Validate<T>(T instance)
    {
        return Validate(new ValidationContext<T>(instance, true)
        {
            ContinueThroughValidationChain = options.ContinueThroughValidationChain,
            ThrowExceptionOnFailure = options.ThrowExceptionOnFailure,
            ValidationMode = options.ValidationMode

        } as IValidationContext);
    }

    /// <inheritdoc cref="IValidator.Validate(IValidationContext)"/>
    public ValidationResult Validate(IValidationContext context)
    {
        var stopwatch = SimpleObjectPool.Rent<Stopwatch>();

        stopwatch.Start();

        //for (int i = 0; i < (Profiles as IList<IValidationProfile>).Count; i++)
        //{
        //    var profile = (Profiles as IList<IValidationProfile>)[i];

        //    if (profile.ValidationType == context.InstanceType)
        //    {
        //        var isModeStop = context.ValidationMode == ValidationMode.Stop;

        //        for (int a = 0; a < profile.ValidationItems.Count; a++)
        //        {
        //            var item = profile.ValidationItems[a];

        //            if (isModeStop && context.Errors.TryGetNonEnumeratedCount(out var count) && count > 0)
        //            {
        //                break;
        //            }

        //            item.Evaluate(context);
        //        }
        //    }
        //}

        foreach (var profile in this.Profiles)
        {
            if (profile.ValidationType == context.InstanceType)
            {
                var isModeStop = context.ValidationMode == ValidationMode.Stop;

                foreach (var item in profile.ValidationItems)
                {
                    if (isModeStop && context.Errors.TryGetNonEnumeratedCount(out var count) && count > 0)
                    {
                        break;
                    }

                    item.Evaluate(context);
                }
            }
        }

        stopwatch.Stop();

        // Let's throw exception for any validation failure if requested.
        if (this.options.ThrowExceptionOnFailure && context.Errors.Any())
        {
            throw new ValidationFailureException(context);
        }

        return new ValidationResult(context, stopwatch.ElapsedTicks);
    }

    /// <inheritdoc cref="IValidator.ValidateAsync{T}(T, CancellationToken)"/>
    public Task<ValidationResult> ValidateAsync<T>(T instance, CancellationToken cancellationToken = default)
    {
        return ValidateAsync(new ValidationContext<T>(instance, true)
        {
            ContinueThroughValidationChain = options.ContinueThroughValidationChain,
            ThrowExceptionOnFailure = options.ThrowExceptionOnFailure,
            ValidationMode = options.ValidationMode

        } as IValidationContext, cancellationToken);
    }

    /// <inheritdoc cref="IValidator.ValidateAsync(IValidationContext, CancellationToken)"/>
    public Task<ValidationResult> ValidateAsync(IValidationContext context, CancellationToken cancellationToken = default)
    {
        return Task.Run<ValidationResult>(() =>
        {
            var stopwatch = SimpleObjectPool.Rent<Stopwatch>();

            stopwatch.Start();

            foreach (var profile in this.Profiles)
            {
                if (profile.ValidationType == context.InstanceType)
                {
                    var isModeStop = context.ValidationMode == ValidationMode.Stop;
                    var tokenSource = cancellationToken == default ?
                        new CancellationTokenSource() :
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    foreach (var item in profile.ValidationItems)
                    {
                        if (tokenSource.IsCancellationRequested)
                        {
                            return default;
                        }
                        if (isModeStop && context.Errors.Any())
                        {
                            break;
                        }

                        item.Evaluate(context);
                    }
                }
            }

            stopwatch.Stop();

            if (this.options.ThrowExceptionOnFailure && context.Errors.Any())
            {
                throw new ValidationFailureException(context);
            }

            return new ValidationResult(context, stopwatch.ElapsedTicks);
        });
    }

    /// <summary>
    /// A fluent API for creating and configuring a new Validator instance.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IValidator Create(Action<ValidatorBuilder> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new ValidatorBuilder();

        configure.Invoke(builder);

        return new Validator(builder.Profiles, builder.Options);
    }
}