using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.RecoveryFixture;

/// <summary>A NativeAOT consumer implementation that receives NULL and MISSING explicitly.</summary>
internal sealed class HasValueFunction : DocumentScalarFunction
{
    internal HasValueFunction() : base("has_value", 1, 1, DatabaseType.Boolean, DocumentNullBehavior.CalledOnNullOrMissingInput) { }
    protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments)
        => DocumentValue.FromBoolean(!arguments.IsMissing(0) && !arguments.IsNull(0));
}
