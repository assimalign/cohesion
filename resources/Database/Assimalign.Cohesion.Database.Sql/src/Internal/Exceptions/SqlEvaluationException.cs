using System;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Language;

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
/// The code is structured in <see cref="DatabaseException.Diagnostics"/>; Message is the
/// unprefixed detail. Planning attaches the offending node's location when available.
/// The wire server composes <c>CODE: message</c> for protocol 1.0 ExecutionFailure.
/// The codes and the ISO SQLSTATE each corresponds to are
/// published in the dialect's diagnostics table.
/// </remarks>
internal sealed class SqlEvaluationException : DatabaseException
{
    /// <summary>The divisor of <c>/</c> or <c>%</c> is zero (ISO SQLSTATE 22012).</summary>
    internal const string DivisionByZeroCode = "COHSQLE001";

    /// <summary>A numeric result or operand is outside its type's range (ISO SQLSTATE 22003).</summary>
    internal const string NumericValueOutOfRangeCode = "COHSQLE002";

    /// <summary>
    /// An operand has the wrong type: unary signs and binary arithmetic require numbers,
    /// and NOT requires a Boolean (ISO SQLSTATE 42804, datatype mismatch).
    /// Raised during planning when the operand's type is known there, and during
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
    /// A function or a cast threw while the statement ran: anything a <see cref="SqlFunction"/>'s
    /// core throws other than cancellation of the statement itself,
    /// <see cref="InsufficientExecutionStackException"/>, <see cref="OutOfMemoryException"/> or a
    /// <see cref="DatabaseException"/>, which is the inner exception (ISO SQLSTATE 38000, external
    /// routine exception); and anything a <see cref="SqlCast"/>'s conversion throws other than those
    /// and a value's fault (<see cref="FormatException"/>, <see cref="OverflowException"/>,
    /// <see cref="InvalidCastException"/>), a cancellation the statement did not ask for included
    /// (phase E3). The statement fails like any other coded failure: it writes nothing, and an
    /// explicit transaction stays usable (owner decision 71 of 2026-10-09).
    /// </summary>
    internal const string FunctionFailedCode = "COHSQLE007";

    /// <summary>
    /// A function call matches more than one overload of its function equally well, so the engine
    /// cannot choose (ISO SQLSTATE 42725, ambiguous function). Raised while planning; a CAST on an
    /// argument chooses (owner decision 71 of 2026-10-09).
    /// </summary>
    internal const string AmbiguousFunctionCallCode = "COHSQLE008";

    /// <summary>
    /// A persisted definition calls a function the engine that opened the database does not
    /// register, or no longer resolves (ISO SQLSTATE 42883, undefined function, as PostgreSQL raises
    /// when a function's implementation is missing at use). The database opens and reads proceed;
    /// a write that would evaluate the definition fails with this code, naming the function, the
    /// table and the constraint, and an engine build that declares the database fails with it
    /// (owner decisions 65 and 71 of 2026-10-09).
    /// </summary>
    internal const string UnregisteredFunctionCode = "COHSQLE009";

    /// <summary>
    /// A type name does not resolve (ISO SQLSTATE 42704, undefined object, as PostgreSQL's "type ...
    /// does not exist"): neither a built-in type nor a domain the engine registers. Raised while
    /// planning for a column type in DDL and a <c>CAST</c> target, the same over an empty table as
    /// over a populated one; and, as decision 65 has it for a function, for a stored column of a
    /// domain the engine that opened the database does not register: the database opens and reads
    /// proceed, a write to the column fails with this code, and an engine build that declares the
    /// database fails with it (phase E3).
    /// </summary>
    internal const string UnregisteredTypeCode = "COHSQLE010";

    /// <summary>
    /// A <c>CAST</c> whose operand's type no registered cast converts to the target (ISO SQLSTATE
    /// 42846, cannot coerce; PostgreSQL's "cannot cast type ... to ...", raised by
    /// <c>transformTypeCast</c>, <c>src/backend/parser/parse_expr.c</c>). Raised while planning when
    /// the plan knows the operand's type, the same over an empty table as over a populated one, and
    /// while evaluating when only the value reveals it, a parameter's (phase E3).
    /// </summary>
    internal const string CannotCastCode = "COHSQLE011";

    /// <summary>A character assignment exceeds its declared length (ISO SQLSTATE 22001).</summary>
    internal const string StringDataRightTruncationCode = "COHSQLE012";

    /// <summary>A CAST input has invalid text representation (PostgreSQL SQLSTATE 22P02).</summary>
    internal const string InvalidTextRepresentationCode = "COHSQLE022";

    /// <summary>A column that disallows NULL received it (SQLSTATE 23502, not_null_violation).</summary>
    internal const string NotNullViolationCode = "COHSQLE024";

    /// <summary>Creates an unlocated NOT NULL failure naming the target column.</summary>
    internal static SqlEvaluationException NotNullViolation(string column, bool missingDefault = false)
        => new(NotNullViolationCode, string.Format(CultureInfo.CurrentCulture,
            missingDefault ? ErrorMessages.ColumnDefaultMissing : ErrorMessages.ColumnNullNotAllowed, column), null);

    /// <summary>Codes a CAST value fault using PostgreSQL's 22003, 22P02 or 42846.</summary>
    internal static SqlEvaluationException CastValueFailed(string detail, Exception exception)
        => new(exception is OverflowException ? NumericValueOutOfRangeCode
            : exception is FormatException ? InvalidTextRepresentationCode : CannotCastCode, detail, exception);

    private readonly string _detail;
    private readonly Location? _location;

    private SqlEvaluationException(string code, string detail, Exception? innerException, Location? location = null)
        : base(detail, [new Diagnostic
        {
            Code = code,
            Message = detail,
            Severity = DiagnosticSeverity.Error,
            Start = location?.Start,
            End = location?.End,
            Line = location?.StartLine ?? 1,
            Location = location is null ? null : DiagnosticLocation.Absolute,
        }], innerException)
    {
        Code = code;
        _detail = detail;
        _location = location;
    }

    /// <summary>Gets the stable engine diagnostic code.</summary>
    internal string Code { get; }

    /// <summary>
    /// Creates a new exception with this one's code, message and inner exception, for a bound node
    /// that raises the same failure on every evaluation: one exception instance must never be thrown
    /// on two threads.
    /// </summary>
    /// <returns>The copy.</returns>
    internal SqlEvaluationException Duplicate() => new(Code, _detail, InnerException, _location);

    /// <summary>Attaches the offending node's location to a planning failure.</summary>
    internal SqlEvaluationException At(Location? location) => _location is not null || location is null
        ? this : new(Code, _detail, InnerException, location);

    /// <summary>Adds assignment context while preserving the diagnostic code and original failure.</summary>
    internal SqlEvaluationException WithContext(string context) => new(Code, $"{context}: {_detail}", this, _location);

    /// <summary>Creates the failure for a character assignment that would lose non-space content.</summary>
    internal static SqlEvaluationException StringDataRightTruncation(int length)
        => new(StringDataRightTruncationCode, $"value too long for type character varying({length}).", null);

    /// <summary>
    /// Whether an exception a function's core threw becomes <c>COHSQLE007</c>: anything but a
    /// cancellation of the statement itself, an exhausted stack or memory, or a database exception, which keep their own
    /// meaning.
    /// </summary>
    /// <param name="exception">What the function threw.</param>
    /// <param name="cancellationToken">The statement's token; default outside a statement.</param>
    /// <returns><see langword="true"/> when the engine codes it as <c>COHSQLE007</c>.</returns>
    internal static bool IsFunctionFailure(Exception exception, CancellationToken cancellationToken)
        => exception is not (InsufficientExecutionStackException or OutOfMemoryException or DatabaseException)
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    /// <summary>
    /// Whether an exception a cast's conversion threw, past a value's fault, becomes <c>COHSQLE007</c>:
    /// anything but an exhausted stack or memory, a database exception, or a cancellation of the
    /// statement itself. The conversion never receives the statement's token, so a cancellation it
    /// throws while the statement was not cancelled is its own fault, which must not end the session
    /// as a cancellation would.
    /// </summary>
    /// <param name="exception">What the conversion threw.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    /// <returns><see langword="true"/> when the engine codes it as <c>COHSQLE007</c>.</returns>
    internal static bool IsCastFailure(Exception exception, CancellationToken cancellationToken)
        => IsFunctionFailure(exception, cancellationToken);

    /// <summary>Creates the failure of a function that threw.</summary>
    /// <param name="functionName">The function's registered name.</param>
    /// <param name="innerException">What the function threw.</param>
    /// <returns>The coded failure.</returns>
    internal static SqlEvaluationException FunctionFailed(string functionName, Exception innerException)
        => new(FunctionFailedCode, $"Function '{functionName}' failed: {innerException.Message}", innerException);

    /// <summary>Creates the failure of a cast whose conversion threw what is not a value's fault.</summary>
    /// <param name="cast">The registered cast.</param>
    /// <param name="innerException">What the conversion threw.</param>
    /// <returns>The coded failure (<c>COHSQLE007</c>).</returns>
    internal static SqlEvaluationException CastFailed(SqlCast cast, Exception innerException)
        => new(FunctionFailedCode, $"Cast {cast} failed: {innerException.Message}", innerException);

    /// <summary>Creates the failure for a <c>CAST</c> no registered cast performs.</summary>
    /// <param name="source">The operand's type, as the dialect names it (a domain by its name).</param>
    /// <param name="target">The target type.</param>
    /// <returns>The coded failure (<c>COHSQLE011</c>).</returns>
    internal static SqlEvaluationException CannotCast(string source, SqlType target)
        => new(CannotCastCode,
            $"Cannot CAST type {source} to {target.Name}: no cast from {source} to {target.Name}" +
            $"{(target.IsDomain ? $" or to its base type {target.BaseType!.Name}" : string.Empty)} is registered. " +
            "Register one on the engine's builder (SqlDatabaseEngineBuilder.Types.AddCast) before Build.",
            null);

    /// <summary>Creates the failure for a call whose argument types no overload of its function accepts.</summary>
    /// <param name="functionName">The function name as written.</param>
    /// <param name="given">The argument types, for example <c>TEXT, BIGINT</c>.</param>
    /// <param name="usage">The accepted call forms.</param>
    /// <returns>The coded failure (<c>COHSQLE006</c>, PostgreSQL's 42883).</returns>
    internal static SqlEvaluationException FunctionArgumentTypeMismatch(string functionName, string given, string usage)
        => new(FunctionSignatureMismatchCode,
            $"Function '{functionName}' has no overload that accepts argument types ({given}). Accepted: {usage}." +
            (functionName.Equals("STRING_AGG", StringComparison.OrdinalIgnoreCase) || functionName.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase)
                ? ErrorMessages.StringAggregateTypeHint : string.Empty),
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

    /// <summary>Creates the failure of a write that would evaluate a persisted definition whose function does not resolve.</summary>
    /// <param name="definition">The definition and the call that does not resolve.</param>
    /// <returns>The coded failure (<c>COHSQLE009</c>).</returns>
    internal static SqlEvaluationException UnregisteredFunction(SqlUnresolvedDefinition definition)
        => new(UnregisteredFunctionCode,
            $"{definition.Describe()}, so the constraint cannot be evaluated and the write is refused. " +
            "Register the function on the engine's builder (SqlDatabaseEngineBuilder.Functions), or drop the constraint.",
            null);

    /// <summary>Creates the failure for a type name that neither a built-in type nor a registered domain has.</summary>
    /// <param name="type">The type as written.</param>
    /// <param name="where">Where it was written, for example <c>CAST target</c>; leads the message.</param>
    /// <returns>The coded failure (<c>COHSQLE010</c>).</returns>
    internal static SqlEvaluationException UnknownType(string type, string where)
        => new(UnregisteredTypeCode,
            $"{where}: type '{type}' does not exist: it is neither a built-in type nor a domain this engine registers. " +
            "Register the domain on the engine's builder (SqlDatabaseEngineBuilder.Types) before Build.",
            null);

    /// <summary>Creates the failure of a write to a stored column whose domain the engine does not register.</summary>
    /// <param name="domain">The column and the domain that does not resolve.</param>
    /// <returns>The coded failure (<c>COHSQLE010</c>).</returns>
    internal static SqlEvaluationException UnregisteredDomain(SqlUnresolvedDomain domain)
        => new(UnregisteredTypeCode,
            $"{domain.Describe()}, so the column cannot be written and the write is refused. " +
            "Register the domain on the engine's builder (SqlDatabaseEngineBuilder.Types), or drop the column.",
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
