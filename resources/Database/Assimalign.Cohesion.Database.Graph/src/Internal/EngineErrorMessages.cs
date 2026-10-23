using ModelErrorMessages = Assimalign.Cohesion.Database.Graph.ErrorMessages;

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
    internal static string EngineDescription => ModelErrorMessages.EngineDescription;
    internal static string OptionPositiveRequirement => ModelErrorMessages.OptionPositiveRequirement;
    internal static string OptionNonnegativeRequirement => ModelErrorMessages.OptionNonnegativeRequirement;
    internal static string OptionMaximumRequirement => ModelErrorMessages.OptionMaximumRequirement;
    internal static string BufferPoolCapacityRequirement => ModelErrorMessages.BufferPoolCapacityRequirement;
    internal static string JournalCheckpointMinimum => ModelErrorMessages.JournalCheckpointMinimum;
    internal static string DatabaseNameRule => ModelErrorMessages.DatabaseNameRule;
    internal static string DatabaseNameEmpty => ModelErrorMessages.DatabaseNameEmpty;
    internal static string DatabaseNameTooLong => ModelErrorMessages.DatabaseNameTooLong;
    internal static string DatabaseNameCharacterInvalid => ModelErrorMessages.DatabaseNameCharacterInvalid;
    internal static string DatabaseNameLeadingDot => ModelErrorMessages.DatabaseNameLeadingDot;
    internal static string DatabaseNameTrailingDot => ModelErrorMessages.DatabaseNameTrailingDot;
    internal static string DatabaseNameConsecutiveDots => ModelErrorMessages.DatabaseNameConsecutiveDots;
    internal static string DatabaseNameDeviceReserved => ModelErrorMessages.DatabaseNameDeviceReserved;
    internal static string FileNamesOrphanDirectory => ModelErrorMessages.FileNamesOrphanDirectory;
}
