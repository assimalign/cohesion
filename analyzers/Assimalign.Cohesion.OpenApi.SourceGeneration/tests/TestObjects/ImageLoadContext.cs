using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration.Tests;

/// <summary>
/// A collectible load context that serves in-memory PE images by assembly name and defers everything
/// else to the default context, so the loaded code shares the test's own OpenApi types. Test-only: it
/// lets a test execute a composed registry, which the shipped code never does by reflection.
/// </summary>
internal sealed class ImageLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, byte[]> _images;

    /// <summary>Initializes a new instance of the <see cref="ImageLoadContext"/> class.</summary>
    /// <param name="images">The PE images to serve, keyed by assembly name.</param>
    public ImageLoadContext(Dictionary<string, byte[]> images)
        : base(isCollectible: true)
    {
        _images = images;
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName) =>
        assemblyName.Name is { } name && _images.TryGetValue(name, out var image)
            ? LoadFromStream(new MemoryStream(image))
            : null;
}
