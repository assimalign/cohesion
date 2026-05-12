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
    /// Specifies whether the validator should continue 
    /// or stop after the first validation item failure.
    /// </summary>
    ValidationMode ValidationMode { get; }

    /// <summary>
    /// Will throw a <see cref="ValidationFailureException"/> rather 
    /// than return <see cref="ValidationResult"/>.
    /// </summary>
    bool ThrowExceptionOnFailure { get; }

    /// <summary>
    /// By default, when more then one rule is chained to a validation item
    /// the first failure will exit the chain. Set this property to true if 
    /// the desired behavior is to iterate through all rules in the validation chain.
    /// <br/>
    /// <br/>
    /// <example>
    /// <b>An example of default behavior:</b>
    /// <code>
    /// RuleFor(p => p.Property)
    ///       .NotNull()     // If this Rule Fails
    ///       .NotEmpty()    // Then this one will not run  
    /// </code>
    /// </example>
    /// </summary>
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