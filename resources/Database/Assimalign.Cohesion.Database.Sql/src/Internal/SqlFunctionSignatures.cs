using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The engine's one table of executable function signatures (#1189). Every reader of a function's
/// shape reads it here, so a function added to the table is checked everywhere at once: the
/// planner, which resolves every call in every expression position before it binds the catalog
/// or reads a row; the evaluator, which checks a call again before it computes it; CHECK
/// validation, which admits the scalars listed here; persisted-definition binding when a database
/// opens; and aggregate detection.
/// </summary>
/// <remarks>
/// <para>
/// The table holds what executes. The SQL profile's function list
/// (<see cref="SqlLanguageProfile.Instance"/>) is the declared vocabulary: a name outside it is
/// unknown, and a declared name outside this table (<c>NULLIF</c>, <c>TRIM</c>, ...) is not executable
/// yet, which shared-diagnostics (#1103) rejects at parse time. Every name here is a declared
/// name.
/// </para>
/// <para>
/// A call matches when its argument count is within its signature's bounds, the test
/// PostgreSQL's <c>func_get_detail</c> applies first (<c>src/backend/parser/parse_func.c</c>); a call
/// no signature accepts fails with <c>COHSQLE006</c>, PostgreSQL's SQLSTATE 42883
/// (<c>function ... does not exist</c>, "No function of that name accepts the given number of
/// arguments"). The T1 scalar functions (#1120) are added as entries of this table, and their
/// argument-type rules, result types, NULL rule and determinism as members of
/// <see cref="SqlFunctionSignature"/>; no second list of names may be introduced.
/// </para>
/// <para>
/// The table is a frozen dictionary built once from a fixed array: no reflection and no code
/// generated at run time, so it is NativeAOT- and trimming-safe. A lookup compares the name
/// case-insensitively without allocating, which the evaluator does once per call per row.
/// </para>
/// </remarks>
internal static class SqlFunctionSignatures
{
    private static readonly FrozenDictionary<string, SqlFunctionSignature> _signatures = new SqlFunctionSignature[]
    {
        // COALESCE takes one or more operands, as PostgreSQL's grammar does (COALESCE '(' expr_list ')'
        // in src/backend/parser/gram.y); ISO's <case abbreviation> requires two or more. One operand
        // is the operand itself, so accepting it loses nothing, and it keeps PostgreSQL text running.
        new(SqlBuiltinFunction.Coalesce, "COALESCE", SqlFunctionKind.Scalar, 1, null, false, "COALESCE(value [, value ...])"),
        new(SqlBuiltinFunction.Upper, "UPPER", SqlFunctionKind.Scalar, 1, 1, false, "UPPER(value)"),
        new(SqlBuiltinFunction.Lower, "LOWER", SqlFunctionKind.Scalar, 1, 1, false, "LOWER(value)"),
        new(SqlBuiltinFunction.Length, "LENGTH", SqlFunctionKind.Scalar, 1, 1, false, "LENGTH(value)"),
        new(SqlBuiltinFunction.Abs, "ABS", SqlFunctionKind.Scalar, 1, 1, false, "ABS(numeric)"),
        new(SqlBuiltinFunction.Count, "COUNT", SqlFunctionKind.Aggregate, 1, 1, true, "COUNT(*) or COUNT(value)"),
        new(SqlBuiltinFunction.Sum, "SUM", SqlFunctionKind.Aggregate, 1, 1, false, "SUM(numeric)"),
        new(SqlBuiltinFunction.Avg, "AVG", SqlFunctionKind.Aggregate, 1, 1, false, "AVG(numeric)"),
        new(SqlBuiltinFunction.Min, "MIN", SqlFunctionKind.Aggregate, 1, 1, false, "MIN(value)"),
        new(SqlBuiltinFunction.Max, "MAX", SqlFunctionKind.Aggregate, 1, 1, false, "MAX(value)"),
    }.ToFrozenDictionary(signature => signature.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets every signature in the table.</summary>
    internal static IReadOnlyCollection<SqlFunctionSignature> All => _signatures.Values;

    /// <summary>Finds the signature of an executable function by name, case-insensitively.</summary>
    /// <param name="name">The function name as written.</param>
    /// <param name="signature">The signature, when the function executes.</param>
    /// <returns><see langword="true"/> when the table holds the function.</returns>
    internal static bool TryGet(string name, [NotNullWhen(true)] out SqlFunctionSignature? signature)
        => _signatures.TryGetValue(name, out signature);

    /// <summary>Whether a name is an aggregate function a grouping plan executes.</summary>
    /// <param name="name">The function name as written.</param>
    /// <returns><see langword="true"/> for <c>COUNT</c>, <c>SUM</c>, <c>AVG</c>, <c>MIN</c> and <c>MAX</c>.</returns>
    internal static bool IsAggregate(string name)
        => _signatures.TryGetValue(name, out var signature) && signature.Kind == SqlFunctionKind.Aggregate;

    /// <summary>Whether a name is a scalar function the row evaluator executes.</summary>
    /// <param name="name">The function name as written.</param>
    /// <returns><see langword="true"/> for a scalar function in the table.</returns>
    internal static bool IsScalar(string name)
        => _signatures.TryGetValue(name, out var signature) && signature.Kind == SqlFunctionKind.Scalar;

    /// <summary>
    /// Matches a call against its function's signature. A name the table does not hold is left to
    /// the caller: the planner reports an undeclared name as unknown, and the evaluator reports a
    /// declared one as not executable yet.
    /// </summary>
    /// <param name="call">The function call.</param>
    /// <returns>The signature the call matches, or null when the table does not hold the name.</returns>
    /// <exception cref="SqlEvaluationException">
    /// The call passes a number of arguments, or a <c>*</c>, its function does not accept (<c>COHSQLE006</c>).
    /// </exception>
    internal static SqlFunctionSignature? Resolve(SqlFunctionCallExpression call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_signatures.TryGetValue(call.FunctionName, out var signature))
        {
            return null;
        }

        if (!signature.Accepts(call.Arguments))
        {
            throw SqlEvaluationException.FunctionSignatureMismatch(call.FunctionName, signature.DescribeArity(),
                SqlFunctionSignature.DescribeArguments(call.Arguments), signature.Usage);
        }

        return signature;
    }
}
