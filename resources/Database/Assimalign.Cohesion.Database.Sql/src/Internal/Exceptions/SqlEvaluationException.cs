using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A data exception raised while evaluating a scalar expression or aggregate, or while
/// storing a numeric value into a column: the statement fails with a stable engine
/// code, and the session and any open transaction stay usable, exactly as for any
/// other statement failure. An operand-type fault the planner can already see is raised
/// while planning, with the same code, so it does not depend on whether rows exist, and a
/// statement too complex to walk fails wherever its walk runs out of stack. A column
/// reference in a clause that has no columns in scope, and a function call whose arguments
/// its function does not accept, are raised while planning, before anything executes.
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
    /// Parsing or running the statement needs more stack than the thread has left (ISO SQLSTATE
    /// 54001, statement too complex): a statement within a high configured nesting limit, a
    /// <c>LIKE</c> match that backtracks through more wildcards than the stack holds, or a
    /// statement run on a thread created with too small a stack, including a stored definition
    /// the statement reads back on first use. The parser and every recursive walk check the stack
    /// before they descend, so the statement fails instead of the process: PostgreSQL's backstop
    /// behind the dialect's nesting limit (#1151).
    /// </summary>
    internal const string StatementTooComplexCode = "COHSQLE004";

    /// <summary>
    /// A column reference appears in a clause that has no columns in scope (ISO SQLSTATE class 42,
    /// syntax error or access rule violation): a row of <c>INSERT ... VALUES</c>, or a
    /// <c>LIMIT</c> or <c>OFFSET</c> count. Raised while planning, so nothing executes (#1165).
    /// </summary>
    internal const string ColumnReferenceNotAllowedCode = "COHSQLE005";

    /// <summary>
    /// A function call passes a number of arguments, or a <c>*</c>, that no signature of its
    /// function accepts: <c>ABS(1, 2)</c>, <c>UPPER()</c>, <c>COALESCE()</c>, <c>COUNT(a, b)</c>,
    /// <c>SUM(*)</c> (ISO SQLSTATE class 42; PostgreSQL's 42883, undefined function). Raised while
    /// planning, in every expression position, and by the evaluator for a call that reaches it
    /// without planning; a persisted definition that holds one fails the database's open (#1189).
    /// </summary>
    internal const string FunctionSignatureMismatchCode = "COHSQLE006";

    /// <summary>
    /// A function threw while the statement ran: anything a <see cref="SqlFunction"/>'s core throws
    /// other than <see cref="OperationCanceledException"/>, <see cref="InsufficientExecutionStackException"/>,
    /// <see cref="OutOfMemoryException"/> or a <see cref="DatabaseException"/>, which is the inner
    /// exception (ISO SQLSTATE 38000, external routine exception). The statement fails like any
    /// other coded failure: it writes nothing, and an explicit transaction stays usable (owner
    /// decision 71 of 2026-10-09).
    /// </summary>
    internal const string FunctionFailedCode = "COHSQLE007";

    /// <summary>
    /// A function call matches more than one overload of its function equally well, so the engine
    /// cannot choose (ISO SQLSTATE 42725, ambiguous function). Raised while planning; a CAST on an
    /// argument chooses (owner decision 71 of 2026-10-09).
    /// </summary>
    internal const string AmbiguousFunctionCallCode = "COHSQLE008";

    private readonly string _detail;

    private SqlEvaluationException(string code, string detail, Exception? innerException)
        : base($"{code}: {detail}", innerException)
    {
        Code = code;
        _detail = detail;
    }

    /// <summary>Gets the stable engine diagnostic code.</summary>
    internal string Code { get; }

    /// <summary>
    /// Creates a new exception with this one's code, message and inner exception, for a bound node
    /// that raises the same failure on every evaluation: one exception instance must never be thrown
    /// on two threads.
    /// </summary>
    /// <returns>The copy.</returns>
    internal SqlEvaluationException Duplicate() => new(Code, _detail, InnerException);

    /// <summary>
    /// Whether an exception a function's core threw becomes <c>COHSQLE007</c>: anything but a
    /// cancellation, an exhausted stack or memory, or a database exception, which keep their own
    /// meaning.
    /// </summary>
    /// <param name="exception">What the function threw.</param>
    /// <returns><see langword="true"/> when the engine codes it as <c>COHSQLE007</c>.</returns>
    internal static bool IsFunctionFailure(Exception exception)
        => exception is not (OperationCanceledException or InsufficientExecutionStackException or OutOfMemoryException or DatabaseException);

    /// <summary>Creates the failure of a function that threw.</summary>
    /// <param name="functionName">The function's registered name.</param>
    /// <param name="innerException">What the function threw.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException FunctionFailed(string functionName, Exception innerException)
        => new(FunctionFailedCode, $"Function '{functionName}' failed: {innerException.Message}", innerException);

    /// <summary>Creates the failure for a call whose argument types no overload of its function accepts.</summary>
    /// <param name="functionName">The function name as written.</param>
    /// <param name="given">The argument types, for example <c>TEXT, BIGINT</c>.</param>
    /// <param name="usage">The accepted call forms.</param>
    /// <returns>The coded failure (<c>COHSQLE006</c>, PostgreSQL's 42883).</returns>
    internal static SqlEvaluationException FunctionArgumentTypeMismatch(string functionName, string given, string usage)
        => new(FunctionSignatureMismatchCode,
            $"Function '{functionName}' has no overload that accepts argument types ({given}). Accepted: {usage}.",
            null);

    /// <summary>Creates the failure for a call that more than one overload accepts equally well.</summary>
    /// <param name="functionName">The function name as written.</param>
    /// <param name="given">The argument types, for example <c>unknown</c>.</param>
    /// <param name="candidates">The overloads that tie, for example <c>f(BIGINT) and f(TEXT)</c>.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException AmbiguousFunctionCall(string functionName, string given, string candidates)
        => new(AmbiguousFunctionCallCode,
            $"Function call '{functionName}({given})' is ambiguous: {candidates} accept it equally well. Cast an argument to choose one.",
            null);

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
    /// <param name="innerException">
    /// The exhausted-stack signal a recursive walk raised; when a stored definition was being read
    /// back, its message names the definition.
    /// </param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException StatementTooComplex(InsufficientExecutionStackException innerException)
        => new(StatementTooComplexCode,
            "Statement too complex: running it needs more stack than the executing thread has left. " +
            "Reduce the nesting of its expressions or the number of wildcards in a LIKE pattern, or run it on a thread with a larger stack.",
            innerException);

    /// <summary>Creates the failure for a column reference in a clause that has no columns in scope.</summary>
    /// <param name="column">The reference as written, qualifiers included.</param>
    /// <param name="clause">The clause, as the dialect names it, for example <c>INSERT ... VALUES</c>.</param>
    /// <param name="alternative">
    /// The clause's own way to read table columns, appended to the advice, or null when it has none.
    /// </param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException ColumnReferenceNotAllowed(string column, string clause, string? alternative = null)
        => new(ColumnReferenceNotAllowedCode,
            $"Column reference '{column}' is not allowed in {clause}, which has no columns in scope. " +
            $"Use literals, parameters and expressions over them{(alternative is null ? string.Empty : $", or {alternative}")}.",
            null);

    /// <summary>Creates the failure for a function call whose arguments no signature of its function accepts.</summary>
    /// <param name="functionName">The function name as written.</param>
    /// <param name="accepted">The argument counts the function accepts, for example <c>exactly 1 argument</c>.</param>
    /// <param name="given">What the call passed: <c>'*'</c>, <c>none</c>, or the number of arguments.</param>
    /// <param name="usage">The accepted call forms, for example <c>ABS(numeric)</c>.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException FunctionSignatureMismatch(string functionName, string accepted, string given, string usage)
        => new(FunctionSignatureMismatchCode,
            $"Function '{functionName}' takes {accepted} but was called with {given}. Accepted: {usage}.",
            null);

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
