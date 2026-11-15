using ModelErrorMessages = Assimalign.Cohesion.Database.Sql.ErrorMessages;

// Deviates from the repo RootNamespace rule per shared-source design: this assembly-local
// resource binding keeps the owning MigrationLowering namespace in each consumer.
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

internal static class SqlMigrationLoweringErrorMessages
{
    internal static string GeneratorRequireMatchingNameFailure => ModelErrorMessages.GeneratorRequireMatchingNameFailure;
    internal static string MigrationScriptGeneratorRenderFailure => ModelErrorMessages.MigrationScriptGeneratorRenderFailure;
    internal static string ProvisionerResolveColumnTypeDatabase2 => ModelErrorMessages.ProvisionerResolveColumnTypeDatabase2;
    internal static string RendererDecimalTypeNameDatabase => ModelErrorMessages.RendererDecimalTypeNameDatabase;
    internal static string ResolveAlterColumnTypeDatabase => ModelErrorMessages.ResolveAlterColumnTypeDatabase;
    internal static string RulesValidateSqlDomainNameArgument => ModelErrorMessages.RulesValidateSqlDomainNameArgument;
    internal static string RulesValidateSqlDomainNameArgument2 => ModelErrorMessages.RulesValidateSqlDomainNameArgument2;
    internal static string RulesValidateSqlDomainNameArgument3 => ModelErrorMessages.RulesValidateSqlDomainNameArgument3;
    internal static string RulesValidateSqlDomainNameArgument4 => ModelErrorMessages.RulesValidateSqlDomainNameArgument4;
    internal static string ScriptGeneratorInvalidPayloadFailure => ModelErrorMessages.ScriptGeneratorInvalidPayloadFailure;
    internal static string StatementRendererIdentifierDatabase => ModelErrorMessages.StatementRendererIdentifierDatabase;
    internal static string StatementRendererTypeNameDatabase => ModelErrorMessages.StatementRendererTypeNameDatabase;
    internal static string StatementRendererTypeNameDatabase2 => ModelErrorMessages.StatementRendererTypeNameDatabase2;
    internal static string TypeRulesValidateDomainNameArgument => ModelErrorMessages.TypeRulesValidateDomainNameArgument;
    internal static string TypeRulesValidateDomainNameArgument2 => ModelErrorMessages.TypeRulesValidateDomainNameArgument2;
}
