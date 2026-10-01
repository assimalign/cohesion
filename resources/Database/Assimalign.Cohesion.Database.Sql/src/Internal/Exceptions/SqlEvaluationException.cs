using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A data exception raised while evaluating a scalar expression or aggregate, or while
/// storing a numeric value into a column: the statement fails with a stable engine
/// code, and the session and any open transaction stay usable, exactly as for any
/// other statement failure. An operand-type fault the planner can already see is raised
/// while planning, with the same code, so it does not depend on whether rows exist, and a
/// statement too complex to walk fails wherever its walk runs out of stack.
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

    /// <summary>
    /// A unary <c>+</c> or <c>-</c> operand is not a number (ISO SQLSTATE 42804, datatype
    /// mismatch). Raised during planning when the operand's type is known there, and during
    /// evaluation when only the value reveals it.
    /// </summary>
    internal const string InvalidOperandTypeCode = "COHSQLE003";

    /// <summary>
    /// Running the statement needs more stack than the thread has left (ISO SQLSTATE 54001,
    /// statement too complex): an expression tree deeper than any the parser accepts, which only
    /// a tree built by hand can be, a <c>LIKE</c> match that backtracks through more wildcards
    /// than the stack holds, or a statement within the limits run on a thread created with too
    /// small a stack. Every recursive walk checks the stack before it descends, so the statement
    /// fails instead of the process (#1151).
    /// </summary>
    internal const string StatementTooComplexCode = "COHSQLE004";

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

    /// <summary>Creates the failure for a unary sign applied to a value that is not a number.</summary>
    /// <param name="operatorText">The sign, <c>+</c> or <c>-</c>.</param>
    /// <param name="operandType">The operand's type, as the dialect names it.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException InvalidOperandType(string operatorText, string operandType)
        => new(InvalidOperandTypeCode, $"Invalid operand type: unary '{operatorText}' requires a numeric operand, but the operand is {operandType}.", null);

    /// <summary>Creates the failure for a statement whose walk ran out of stack.</summary>
    /// <param name="innerException">The exhausted-stack signal a recursive walk raised.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException StatementTooComplex(InsufficientExecutionStackException innerException)
        => new(StatementTooComplexCode,
            "Statement too complex: running it needs more stack than the engine has available. " +
            "Reduce the nesting of its expressions, or the number of wildcards in a LIKE pattern.",
            innerException);

    /// <summary>
    /// Codes a runtime arithmetic fault that no evaluation site coded at its source,
    /// such as an oversized numeric literal, whose message names the literal. Division
    /// faults keep their own code.
    /// </summary>
    /// <param name="exception">The runtime arithmetic fault.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException FromArithmetic(ArithmeticException exception)
        => exception is DivideByZeroException
            ? new(DivisionByZeroCode, "Division by zero.", exception)
            : NumericValueOutOfRange(exception.Message, exception);
}
