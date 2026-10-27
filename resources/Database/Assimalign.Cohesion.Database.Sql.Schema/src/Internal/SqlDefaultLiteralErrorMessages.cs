using ModelErrorMessages = Assimalign.Cohesion.Database.Sql.Schema.ErrorMessages;

// Deviates from the repo RootNamespace rule per shared-source design: this assembly-local
// resource binding keeps the owning Defaults module namespace in each consumer.
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

internal static class SqlDefaultLiteralErrorMessages
{
    internal static string DefaultValueIncompatible => ModelErrorMessages.DefaultValueIncompatible;
}
