using System;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public abstract class ValidationException : Exception
{
    private const string messageDefault = "An unknown error was caught.";

    /// <summary>
    /// 
    /// </summary>
    private ValidationException() : this(messageDefault) { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    public ValidationException(string message) : base(message) { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <param name="inner"></param>
    internal ValidationException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// A internal validation exception code.
    /// </summary>
    public ValidationExceptionCode ErrorCode { get; set; } = ValidationExceptionCode.UnknownError;

    /// <summary>
    /// The source of the exception.
    /// </summary>
    public override string Source { get; set; }
}