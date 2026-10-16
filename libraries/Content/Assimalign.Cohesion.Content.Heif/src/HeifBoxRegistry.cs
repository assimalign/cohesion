using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>Composes the HEIF item grammar into an explicit, AOT-safe BMFF registry builder.</summary>
public static class HeifBoxRegistry
{
    /// <summary>Gets the immutable HEIF registry with default limits and the BMFF defaults.</summary>
    public static BmffBoxRegistry Default { get; } = Register(BmffBoxRegistry.CreateBuilder()).Build();
    /// <summary>Adds or replaces the HEIF registrations on a caller's builder.</summary>
    /// <param name="builder">The mutable builder, normally seeded by BmffBoxRegistry.CreateBuilder.</param>
    /// <param name="options">HEIF limits or defaults.</param>
    /// <returns>The same builder for further profile composition.</returns>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is negative or unrepresentable.</exception>
    public static BmffBoxRegistryBuilder Register(BmffBoxRegistryBuilder builder, HeifReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new HeifReaderOptions();
        options.Validate();
        builder.Register((BmffBoxType)0x69707270, _ => new ItemPropertiesBox { Options = options });
        builder.Register((BmffBoxType)0x6970636f, _ => new ItemPropertyContainerBox { Options = options });
        builder.Register((BmffBoxType)0x69706d61, _ => new ItemPropertyAssociationBox { Options = options });
        builder.Register((BmffBoxType)0x69726566, _ => new ItemReferenceBox { Options = options });
        builder.Register((BmffBoxType)0x74686d62, _ => new ThumbnailReferenceBox { Options = options });
        builder.Register((BmffBoxType)0x6175786c, _ => new AuxiliaryReferenceBox { Options = options });
        builder.Register((BmffBoxType)0x64696d67, _ => new DerivedImageReferenceBox { Options = options });
        builder.Register((BmffBoxType)0x63647363, _ => new ContentDescriptionReferenceBox { Options = options });
        builder.Register((BmffBoxType)0x62617365, _ => new BaseReferenceBox { Options = options });
        builder.Register((BmffBoxType)0x69737065, _ => new ImageSpatialExtentsBox { Options = options });
        builder.Register((BmffBoxType)0x70697869, _ => new PixelInformationBox { Options = options });
        builder.Register((BmffBoxType)0x636f6c72, _ => new ColourInformationBox { Options = options });
        builder.Register((BmffBoxType)0x69726f74, _ => new ImageRotationBox { Options = options });
        builder.Register((BmffBoxType)0x696d6972, _ => new ImageMirrorBox { Options = options });
        builder.Register((BmffBoxType)0x636c6170, _ => new CleanApertureBox { Options = options });
        builder.Register((BmffBoxType)0x61757843, _ => new AuxiliaryTypeBox { Options = options });
        builder.Register((BmffBoxType)0x70617370, _ => new PixelAspectRatioBox { Options = options });
        builder.Register((BmffBoxType)0x68766343, _ => new HevcConfigurationBox { Options = options });
        builder.Register((BmffBoxType)0x61763143, _ => new Av1ConfigurationBox { Options = options });
        builder.Register((BmffBoxType)0x69646174, _ => new ItemDataBox { Options = options });
        return builder;
    }
}
