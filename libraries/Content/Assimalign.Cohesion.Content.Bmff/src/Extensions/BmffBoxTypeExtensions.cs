using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Lossless conversions between BMFF types and binary FourCC values.</summary>
public static class BmffBoxTypeExtensions
{
    extension(BmffBoxType type)
    {
        /// <summary>Gets the four raw bytes, including for unregistered types.</summary>
        /// <returns>The FourCC.</returns>
        public FourCC ToFourCC() => FourCC.FromUInt32((uint)type);
    }

    extension(FourCC type)
    {
        /// <summary>Gets the enum representation without requiring registration.</summary>
        /// <returns>The raw BMFF type.</returns>
        public BmffBoxType ToBmffBoxType() => (BmffBoxType)type.Value;
    }
}
