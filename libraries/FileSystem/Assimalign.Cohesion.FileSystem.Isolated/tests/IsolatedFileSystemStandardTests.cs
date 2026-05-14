using System;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Tests;

/// <summary>
/// Runs the provider-agnostic <see cref="FileSystemStandardTests"/> contract suite against
/// <see cref="IsolatedFileSystem"/>. Each test clears the per-user assembly isolated store before
/// returning a fresh provider so the suite runs from a known-empty baseline.
/// </summary>
public class IsolatedFileSystemStandardTests : FileSystemStandardTests
{
    public override IFileSystem GetFileSystem()
        => Isolated.Tests.IsolatedFileSystemTestFixture.CreateFreshFileSystem();
}
