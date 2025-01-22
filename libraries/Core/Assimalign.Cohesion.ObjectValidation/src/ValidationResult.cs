using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public sealed class ValidationResult
{
    private ValidationResult() { }

    internal ValidationResult(IValidationContext context, double? elapsedTicks = null)
    {
        this.Errors = context.Errors;
        this.Invocations = context.Invocations;
        this.ValidationElapsedTicks = elapsedTicks;
    }


    /// <summary>
    /// An indicator of whether the type validated was successful.
    /// </summary>
    public bool IsValid => Errors.Count() == 0;
    /// <summary>
    /// A collection of validation failures.
    /// </summary>
    public IEnumerable<IValidationError> Errors { get; }
    /// <summary>
    /// A collection of stats relating to invoked validation rules.
    /// </summary>
    public IEnumerable<ValidationInvocation> Invocations { get; }
    /// <summary>
    /// The total ticks elapsed for validation.
    /// </summary>
    public double? ValidationElapsedTicks { get; }
    /// <summary>
    /// The total milliseconds elapsed for validation.
    /// </summary>
    public double? ValidationElapsedMilliseconds => this.ValidationElapsedTicks is null ? null : this.ValidationElapsedTicks / (double)TimeSpan.TicksPerMillisecond;
    /// <summary>
    /// The total seconds elapsed for validation.
    /// </summary>
    /// <remarks>
    /// Validation should never take this long in terms of contract testing. If validation is 
    /// totaling to seconds try refactoring code.
    /// </remarks>
    public double? ValidationElapsedSeconds => this.ValidationElapsedTicks is null ? null : this.ValidationElapsedTicks / (double)TimeSpan.TicksPerSecond;
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="context"></param>
    /// <returns></returns>
    public static ValidationResult Create(IValidationContext context) => new ValidationResult(context);
}
