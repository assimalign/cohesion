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
    internal static string CanonicalizeLiteral(SqlExpression expression) => SqlExpressionRenderer.Render(expression);
}
