using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public interface IValidationContext
{
    /// <summary>
    /// The instance to apply the validation rules that match the instance type.
    /// </summary>
    object Instance { get; }

    /// <summary>
    /// The instance type being validated.
    /// </summary>
    Type InstanceType { get; }

    /// <summary>
    /// A collection of invocation stats.
    /// </summary>
    IEnumerable<ValidationInvocation> Invocations { get; }

    /// <summary>
    /// A collection of validation failures.
    /// </summary>
    IEnumerable<IValidationError> Errors { get; }

    /// <summary>
    /// Specifies whether the validator evaluates every validation item and reports each one that fails
    /// (<see cref="ValidationMode.Cascade"/>, the default), or evaluates no further item once one has
    /// failed (<see cref="ValidationMode.Stop"/>).
    /// </summary>
    /// <remarks>
    /// The mode decides between items (members). How many of one item's chained rules run is decided by
    /// <see cref="ContinueThroughValidationChain"/>.
    /// </remarks>
    ValidationMode ValidationMode { get; }

    /// <summary>
    /// Will throw a <see cref="ValidationFailureException"/> rather 
    /// than return <see cref="ValidationResult"/>.
    /// </summary>
    bool ThrowExceptionOnFailure { get; }

    /// <summary>
    /// Specifies whether every rule chained to a validation item runs (<see langword="true"/>), or the
    /// item's chain stops once one of its rules has failed (<see langword="false"/>, the default).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chain stops on the item's own failure only. An error another item reported, or one already in
    /// the context, never keeps an item's rules from running: stopping between items is
    /// <see cref="ValidationMode.Stop"/>. With the defaults, every failing item is therefore reported, each
    /// with the errors of one rule.
    /// </para>
    /// <para>
    /// A collection item (<c>RuleForEach</c>) runs each rule over every element, so the rule that fails
    /// reports each failing element before the chain stops.
    /// </para>
    /// </remarks>
    /// <example>
    /// <b>With the default:</b>
    /// <code>
    /// RuleFor(p => p.Name)
    ///       .NotEmpty()
    ///       .MaxLength(50);   // Once one of Name's rules fails, Name's chain stops.
    ///
    /// RuleFor(p => p.Email)
    ///       .NotEmpty();      // Email's rules run whether or not Name failed.
    /// </code>
    /// </example>
    bool ContinueThroughValidationChain { get; }

    /// <summary>
    /// Adds a generic validation failure to <see cref="IValidationContext.Errors"/>
    /// </summary>
    /// <param name="message"></param>
    void AddFailure(string message);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="source"></param>
    /// <param name="message"></param>
    void AddFailure(string source, string message);

    /// <summary>
    /// Adds a validation failure to <see cref="IValidationContext.Errors"/>
    /// </summary>
    /// <param name="error">A description of the validation error.</param>
    void AddFailure(IValidationError error);

    /// <summary>
    /// Adds a rule of a successful validation.
    /// </summary>
    /// <param name="invocation"></param>
    void AddInvocation(ValidationInvocation invocation);
}