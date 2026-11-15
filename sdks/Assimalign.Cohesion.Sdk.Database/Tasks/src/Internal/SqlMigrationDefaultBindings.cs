using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;

using ModelErrorMessages = Assimalign.Cohesion.Sdk.Database.Tasks.ErrorMessages;

// Deviates from the repo RootNamespace rule per shared-source design: this assembly-local
// DEFAULT binding keeps the owning MigrationLowering namespace in each consumer.
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

internal static class SqlMigrationDefaultBindings
{
    internal static string ScalarExpressionRequired => ModelErrorMessages.MigrationScalarExpressionRequired;
    internal static Exception InsufficientStack() => new InsufficientExecutionStackException(ModelErrorMessages.ExpressionParseDeclarationFailure);
    internal static Exception InvalidDefinition(string text, string? problem)
        => new DatabaseException(string.Format(CultureInfo.CurrentCulture, ModelErrorMessages.ExpressionDeclarationInvalid, text, problem));
    internal static Exception LiteralRequired() => new DatabaseException(ModelErrorMessages.ProvisionerCanonicalDefaultDatabase);
    internal static Exception SequenceUnsupported(SqlExpression expression)
    {
        _ = expression;
        return new DatabaseException(ModelErrorMessages.SequencePersistedUnsupported);
    }
    internal static void ObserveNode(SqlExpression expression) => _ = expression;
    internal static string CanonicalizeLiteral(SqlExpression expression)
    {
        string text;
        try { text = SqlExpressionRenderer.Render(expression); }
        catch (NotSupportedException exception)
        {
            throw new DatabaseException(string.Format(CultureInfo.CurrentCulture, ModelErrorMessages.ExpressionStoreUnsupported,
                ModelErrorMessages.CompiledDefaultSubject, exception.Message), exception);
        }
        bool parsed = SqlMigrationDefaultLiteral.TryParse(text, out var reloaded, out string? problem, out bool outOfStack, useCeiling: true);
        if (outOfStack)
        {
            throw new InsufficientExecutionStackException(string.Format(CultureInfo.CurrentCulture,
                ModelErrorMessages.ExpressionCanonicalizeFailure, ModelErrorMessages.CompiledDefaultSubject));
        }
        // Sql.Language exposes no tree equivalence API. Literal rendering must reach a fixed
        // point after reparsing; the engine additionally compares its expression trees.
        if (!parsed || !string.Equals(SqlExpressionRenderer.Render(reloaded!), text, StringComparison.Ordinal))
        {
            throw new DatabaseException(string.Format(CultureInfo.CurrentCulture, ModelErrorMessages.ExpressionRoundTripFailed,
                ModelErrorMessages.CompiledDefaultSubject, text, problem is null ? string.Empty : $" ({problem})"));
        }
        return text;
    }
}
