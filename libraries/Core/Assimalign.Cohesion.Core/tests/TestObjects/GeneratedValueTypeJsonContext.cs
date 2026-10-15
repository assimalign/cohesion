using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Core.Tests;

[JsonSerializable(typeof(GeneratedInt))]
[JsonSerializable(typeof(GeneratedShort))]
[JsonSerializable(typeof(GeneratedLong))]
[JsonSerializable(typeof(GeneratedUInt))]
[JsonSerializable(typeof(GeneratedUShort))]
[JsonSerializable(typeof(GeneratedULong))]
[JsonSerializable(typeof(GeneratedDouble))]
[JsonSerializable(typeof(GeneratedDecimal))]
[JsonSerializable(typeof(GeneratedString))]
[JsonSerializable(typeof(GeneratedGuid))]
[JsonSerializable(typeof(GeneratedUlid))]
[JsonSerializable(typeof(GeneratedValidatedString))]
[JsonSerializable(typeof(GeneratedRedactedString))]
[JsonSerializable(typeof(GeneratedRangeInt))]
[JsonSerializable(typeof(GeneratedIgnoreCaseString))]
[JsonSerializable(typeof(GeneratedStringClass))]
[JsonSerializable(typeof(GeneratedIntClass))]
[JsonSerializable(typeof(GeneratedExplicitInt))]
[JsonSerializable(typeof(Dictionary<GeneratedInt, string>), TypeInfoPropertyName = "IntDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedShort, string>), TypeInfoPropertyName = "ShortDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedLong, string>), TypeInfoPropertyName = "LongDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedUInt, string>), TypeInfoPropertyName = "UIntDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedUShort, string>), TypeInfoPropertyName = "UShortDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedULong, string>), TypeInfoPropertyName = "ULongDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedDouble, string>), TypeInfoPropertyName = "DoubleDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedDecimal, string>), TypeInfoPropertyName = "DecimalDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedString, string>), TypeInfoPropertyName = "StringDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedGuid, string>), TypeInfoPropertyName = "GuidDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedUlid, string>), TypeInfoPropertyName = "UlidDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedValidatedString, string>), TypeInfoPropertyName = "ValidatedStringDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedRangeInt, string>), TypeInfoPropertyName = "RangeIntDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedIgnoreCaseString, string>), TypeInfoPropertyName = "IgnoreCaseStringDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedStringClass, string>), TypeInfoPropertyName = "StringClassDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedIntClass, string>), TypeInfoPropertyName = "IntClassDictionary")]
[JsonSerializable(typeof(Dictionary<GeneratedExplicitInt, string>), TypeInfoPropertyName = "ExplicitIntDictionary")]
internal partial class GeneratedValueTypeJsonContext : JsonSerializerContext
{
}
