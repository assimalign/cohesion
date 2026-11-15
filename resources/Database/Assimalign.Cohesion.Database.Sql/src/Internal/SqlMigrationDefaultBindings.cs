using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Internal;

using ModelErrorMessages = Assimalign.Cohesion.Database.Sql.ErrorMessages;

// Deviates from the repo RootNamespace rule per shared-source design: this assembly-local
// DEFAULT binding keeps the owning MigrationLowering namespace in each consumer.
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

internal static class SqlMigrationDefaultBindings
{
    internal static string ScalarExpressionRequired => ModelErrorMessages.MigrationScalarExpressionRequired;
    internal static Exception InsufficientStack() => new InsufficientExecutionStackException(ModelErrorMessages.ExpressionParseDeclarationFailure);
    internal static Exception InvalidDefinition(string text, string? problem)
        => new SqlExecutionException(SqlExecutionException.InvalidDefinitionCode,
            string.Format(CultureInfo.CurrentCulture, ModelErrorMessages.ExpressionDeclarationInvalid, text, problem));
    internal static Exception LiteralRequired()
        => new SqlExecutionException(SqlProvisioningCodes.PolicyRefused, ModelErrorMessages.ProvisionerCanonicalDefaultDatabase);
    internal static Exception SequenceUnsupported(SqlExpression expression)
        => new SqlPlanningException("CS03074", ModelErrorMessages.SequencePersistedUnsupported, expression.Location);
    internal static void ObserveNode(SqlExpression expression)
    {
        _ = expression;
        SqlSequenceExecution.PersistedNodeVisited?.Invoke();
    }
    internal static string CanonicalizeLiteral(SqlExpression expression)
        => SqlPersistedExpression.Canonicalize(expression, ModelErrorMessages.CompiledDefaultSubject);
}
