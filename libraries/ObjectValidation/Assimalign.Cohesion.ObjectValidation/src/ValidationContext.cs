
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Globalization;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class ValidationContext<T> : IValidationContext
{
    private readonly Type _type;

    // Queues, not stacks: errors and invocations enumerate in the order they were added, which is the order
    // the rules ran. A nested profile's errors are copied into each enclosing context in that order too, so
    // they stay in place under their parent member.
    private ConcurrentQueue<IValidationError>? _errors;
    private ConcurrentQueue<ValidationInvocation>? _invocations;

    private ConcurrentQueue<IValidationError> ErrorQueue => LazyInitializer.EnsureInitialized(ref _errors);
    private ConcurrentQueue<ValidationInvocation> InvocationQueue => LazyInitializer.EnsureInitialized(ref _invocations);

    // Only internal, immediate enumeration may observe this empty view. Public callers receive the
    // stable live queue, so retaining Errors before a later AddFailure keeps its original behavior.
    internal IEnumerable<IValidationError> GetErrors() => _errors is { } errors ? errors : Array.Empty<IValidationError>();

    private ValidationContext() { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <exception cref="ArgumentNullException">An exception is thrown if the <paramref name="instance"/> is null.</exception>
    public ValidationContext(T instance)
    {
        this._type = typeof(T);

        Instance = instance;
    }


    internal ValidationContext(T instance, bool throwExceptionForNullInstance)
    {
        if (throwExceptionForNullInstance && instance is null)
        {
            throw new ArgumentNullException(
                paramName: nameof(instance),
                message: string.Format(CultureInfo.CurrentCulture, ErrorMessages.ValidationInstanceNull, typeof(T).Name));
        } 
        this._type = typeof(T);

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
    public Type InstanceType => this._type;

    /// <summary>
    /// The validation failures that occurred, in the order they were added: the order the rules that
    /// reported them ran.
    /// </summary>
    public IEnumerable<IValidationError> Errors => ErrorQueue;

    /// <summary>
    /// The rule invocations, in the order they were added: the order the rules ran.
    /// </summary>
    public IEnumerable<ValidationInvocation> Invocations => InvocationQueue;

   
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
    public void AddFailure(IValidationError error) => ErrorQueue.Enqueue(new ValidationError(error));

    /// <summary>
    /// 
    /// </summary>
    /// <param name="failureMessage"></param>
    public void AddFailure(string failureMessage)
    {
        ErrorQueue.Enqueue(new ValidationError()
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
        ErrorQueue.Enqueue(new ValidationError()
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
        InvocationQueue.Enqueue(invocation);
    }
}
