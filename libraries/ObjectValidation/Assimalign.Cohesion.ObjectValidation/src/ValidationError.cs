using System;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// an implementation of a validation failure.
/// </summary>
public sealed class ValidationError : IValidationError
{

    /// <summary>
    /// The default constructor.
    /// </summary>
    public ValidationError() { }

    internal ValidationError(IValidationError error)
    {
        this.Code = error.Code;
        this.Message = error.Message;
        this.Source = error.Source;
    }


    /// <inheritdoc cref="IValidationError.Code"/>
    public string Code { get; set; } = "400";

    /// <inheritdoc cref="IValidationError.Message"/>
    public string Message { get; set; }

    /// <inheritdoc cref="IValidationError.Source"/>
    public string Source { get; set; }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"Error {Code}: {Message} {Environment.NewLine} └─> Source: {Source}";
    }
}

