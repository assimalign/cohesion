using ModelErrorMessages = Assimalign.Cohesion.Sdk.Database.Tasks.ErrorMessages;

// Shared Defaults retains its owning namespace in each consuming assembly.
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

internal static class SqlDefaultLiteralErrorMessages
{
    internal static string DefaultValueIncompatible => ModelErrorMessages.DefaultValueIncompatible;
}
