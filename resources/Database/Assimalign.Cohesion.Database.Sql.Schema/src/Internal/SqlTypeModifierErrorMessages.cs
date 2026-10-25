using ModelErrorMessages = Assimalign.Cohesion.Database.Sql.Schema.ErrorMessages;

// Deviates from the repo RootNamespace rule per shared-source design: this assembly-local
// resource binding keeps the owning Types module namespace in each consumer.
namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

internal static class SqlTypeModifierErrorMessages
{
    internal static string TypeArgumentShape => ModelErrorMessages.TypeArgumentShape;
    internal static string TimeZoneStorageUnsupported => ModelErrorMessages.TimeZoneStorageUnsupported;
    internal static string BitStorageUnsupported => ModelErrorMessages.BitStorageUnsupported;
    internal static string SmallDateTimeUnsupported => ModelErrorMessages.SmallDateTimeUnsupported;
    internal static string SerialTypeUnsupported => ModelErrorMessages.SerialTypeUnsupported;
    internal static string IntervalPrecisionArgument => ModelErrorMessages.IntervalPrecisionArgument;
    internal static string IntervalFieldUnsupported => ModelErrorMessages.IntervalFieldUnsupported;
    internal static string FloatArgumentCount => ModelErrorMessages.FloatArgumentCount;
    internal static string FloatPrecisionMinimum => ModelErrorMessages.FloatPrecisionMinimum;
    internal static string FloatPrecisionMaximum => ModelErrorMessages.FloatPrecisionMaximum;
    internal static string TemporalPrecisionArgument => ModelErrorMessages.TemporalPrecisionArgument;
    internal static string TemporalPrecisionUnsupported => ModelErrorMessages.TemporalPrecisionUnsupported;
    internal static string DecimalLengthInvalid => ModelErrorMessages.DecimalLengthInvalid;
    internal static string DecimalPrecisionInvalid => ModelErrorMessages.DecimalPrecisionInvalid;
    internal static string DecimalScaleInvalid => ModelErrorMessages.DecimalScaleInvalid;
    internal static string TypeLengthInvalid => ModelErrorMessages.TypeLengthInvalid;
    internal static string TypeArgumentsUnsupported => ModelErrorMessages.TypeArgumentsUnsupported;
}
