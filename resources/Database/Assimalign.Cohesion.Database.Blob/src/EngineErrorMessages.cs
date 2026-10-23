using ModelErrorMessages = Assimalign.Cohesion.Database.Blob.ErrorMessages;

// Deviates from RootNamespace: this assembly-local binding belongs to the shared Engine module
// and keeps its owning Database namespace, as the module's linked types do.
namespace Assimalign.Cohesion.Database;

// Forward only to this assembly's build-generated accessor; no cross-assembly internal access.
internal static class EngineErrorMessages
{
    internal static string BuilderStateEnsureMutableFailure => ModelErrorMessages.BuilderStateEnsureMutableFailure;
    internal static string DatabaseDeclarationsDeclareFailure => ModelErrorMessages.DatabaseDeclarationsDeclareFailure;
    internal static string DatabaseDeclarationsRefuseDropFailure => ModelErrorMessages.DatabaseDeclarationsRefuseDropFailure;
    internal static string DatabaseMemoryFilesThrowIfOpenFailure => ModelErrorMessages.DatabaseMemoryFilesThrowIfOpenFailure;
    internal static string DatabaseMemoryFilesTryOpenFailure => ModelErrorMessages.DatabaseMemoryFilesTryOpenFailure;
    internal static string DeclarationsAlreadyDeclaredFailure => ModelErrorMessages.DeclarationsAlreadyDeclaredFailure;
    internal static string EngineBuilderStateBeginBuildFailure => ModelErrorMessages.EngineBuilderStateBeginBuildFailure;
    internal static string EngineBuilderStateCompleteFailure => ModelErrorMessages.EngineBuilderStateCompleteFailure;
    internal static string EngineBuilderStateProduceFailure => ModelErrorMessages.EngineBuilderStateProduceFailure;
    internal static string EngineBuilderStateProduceFailure2 => ModelErrorMessages.EngineBuilderStateProduceFailure2;
    internal static string EngineOptionChecksRefuseFailure => ModelErrorMessages.EngineOptionChecksRefuseFailure;
    internal static string FileNamesThrowIfInvalidArgument => ModelErrorMessages.FileNamesThrowIfInvalidArgument;
    internal static string StateThrowIfPendingUnattachedFailure => ModelErrorMessages.StateThrowIfPendingUnattachedFailure;
}
