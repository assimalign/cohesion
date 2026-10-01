using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A data exception raised while evaluating a scalar expression or aggregate: the
/// statement fails with a stable engine code, and the session and any open
/// transaction stay usable, exactly as for any other statement failure.
/// </summary>
/// <remarks>
/// The code leads the message (<c>COHSQLE001: ...</c>), the engine-code convention
/// <see cref="DatabaseException"/> carries until the area root grows a structured
/// diagnostics carrier. The wire server forwards the message unchanged as an
/// <c>ExecutionFailure</c>. The codes and the ISO SQLSTATE each corresponds to are
/// published in the dialect's diagnostics table.
/// </remarks>
internal sealed class SqlEvaluationException : DatabaseException
{
    /// <summary>The divisor of <c>/</c> or <c>%</c> is zero (ISO SQLSTATE 22012).</summary>
    internal const string DivisionByZeroCode = "COHSQLE001";

    /// <summary>A numeric result or operand is outside its type's range (ISO SQLSTATE 22003).</summary>
    internal const string NumericValueOutOfRangeCode = "COHSQLE002";

    private SqlEvaluationException(string code, string detail, Exception? innerException)
        : base($"{code}: {detail}", innerException)
    {
        Code = code;
    }

    /// <summary>Gets the stable engine diagnostic code.</summary>
    internal string Code { get; }

    /// <summary>Creates the division-by-zero failure for a <c>/</c> or <c>%</c> operator.</summary>
    /// <param name="operatorText">The SQL operator whose right operand is zero.</param>
    /// <param name="innerException">The runtime fault, when one was raised.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException DivisionByZero(string operatorText, Exception? innerException = null)
        => new(DivisionByZeroCode, $"Division by zero: the right operand of '{operatorText}' is zero.", innerException);

    /// <summary>Creates the out-of-range failure for a numeric result or operand.</summary>
    /// <param name="detail">What overflowed, without the code prefix.</param>
    /// <param name="innerException">The runtime fault, when one was raised.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException NumericValueOutOfRange(string detail, Exception? innerException = null)
        => new(NumericValueOutOfRangeCode, $"Numeric value out of range: {detail}", innerException);

    /// <summary>
    /// Codes a runtime arithmetic fault that no evaluation site coded at its source,
    /// such as an oversized numeric literal. Division faults keep their own code.
    /// </summary>
    /// <param name="exception">The runtime arithmetic fault.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException FromArithmetic(ArithmeticException exception)
        => exception is DivideByZeroException
            ? new(DivisionByZeroCode, "Division by zero.", exception)
            : NumericValueOutOfRange(exception.Message, exception);
}
