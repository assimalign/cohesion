using System;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Tests;

/// <summary>
/// Runs the provider-agnostic <see cref="FileSystemStandardTests"/> contract suite against an
/// <see cref="AggregateFileSystem"/> with a single <see cref="InMemoryFileSystem"/> mounted at
/// the root. This exercises the routing fast-path (a single longest-prefix hit at "/") plus the
/// wrapper layer that translates paths between the aggregate and the underlying provider.
/// </summary>
public class AggregateFileSystemStandardTests : FileSystemStandardTests
{
    public override IFileSystem GetFileSystem()
    {
        var backing = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            Size = Size.FromMegabytes(8),
        });

        return new AggregateFileSystemBuilder()
            .Mount("/", backing, ownsFileSystem: true)
            .Build();
    }
}
