
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class ValidationContext<T> : IValidationContext
{
    private readonly Type type;
    private readonly ConcurrentStack<IValidationError> errors;
    private readonly ConcurrentStack<ValidationInvocation> invocations;

    private ValidationContext() { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <exception cref="ArgumentNullException">An exception is thrown if the <paramref name="instance"/> is null.</exception>
    public ValidationContext(T instance)
    {
        this.type = typeof(T);
        this.errors = new ConcurrentStack<IValidationError>();
        this.invocations = new ConcurrentStack<ValidationInvocation>();

        Instance = instance;
    }


    internal ValidationContext(T instance, bool throwExceptionForNullInstance)
    {
        if (throwExceptionForNullInstance && instance is null)
        {
            throw new ArgumentNullException(
                paramName: nameof(instance),
                message: $"The instance of type '{typeof(T).Name}' cannot be null");
        } 
        this.type = typeof(T);
        this.errors = new ConcurrentStack<IValidationError>();
        this.invocations = new ConcurrentStack<ValidationInvocation>();

        Instance = instance;
    }

    /// <summary>
    /// The instance to validate.
    /// </summary>
    public T Instance { get; }

    object IValidationContext.Instance => this.Instance;

    /// <summary>
    /// The <see cref="Type"/> of the instance being validated.
    /// </summary>
    public Type InstanceType => this.type;

    /// <summary>
    /// A collection of validation failures that occurred.
    /// </summary>
    public IEnumerable<IValidationError> Errors => this.errors;

    /// <summary>
    /// A collection of invoked 
    /// </summary>
    public IEnumerable<ValidationInvocation> Invocations => this.invocations;

   
    /// <inheritdoc cref="IValidationContext.ThrowExceptionOnFailure"/>
    public bool ThrowExceptionOnFailure { get; init; }

    /// <inheritdoc cref="IValidationContext.ContinueThroughValidationChain"/>
    public bool ContinueThroughValidationChain { get; init; }

    /// <inheritdoc cref="IValidationContext.ValidationMode"/>
    public ValidationMode ValidationMode { get; init; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="error"></param>
    public void AddFailure(IValidationError error) => this.errors.Push(new ValidationError(error));

    /// <summary>
    /// 
    /// </summary>
    /// <param name="failureMessage"></param>
    public void AddFailure(string failureMessage)
    {
        errors.Push(new ValidationError()
        {
            Message = failureMessage
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="failureSource"></param>
    /// <param name="failureMessage"></param>
    public void AddFailure(string failureSource, string failureMessage)
    {
        errors.Push(new ValidationError()
        {
            Message = failureMessage,
            Source = failureSource
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="invocation"></param>
    public void AddInvocation(ValidationInvocation invocation)
    {
        this.invocations.Push(invocation);
    }
}
