using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.FileSystem.Internal;

namespace Assimalign.Cohesion.FileSystem.Aggregate.Tests;

/// <summary>
/// Direct unit tests for the path-routing helpers used by <see cref="AggregateFileSystem"/>.
/// These cover the longest-prefix rules, synthetic-intermediate detection, and synthetic-child
/// enumeration in isolation so failures pinpoint the bug location instead of bubbling up
/// through the full aggregate surface.
/// </summary>
public class AggregateRouterTests
{
    private static AggregateMount Mount(string mountPath)
        => new(mountPath, new InMemoryFileSystem(new InMemoryFileSystemOptions()), ownsFileSystem: true);

    [Theory(DisplayName = "Cohesion Test [AggregateRouter] - Resolve: longest prefix wins")]
    [InlineData("/data/cache/x", "/data/cache")]
    [InlineData("/data/x", "/data")]
    [InlineData("/data", "/data")]
    [InlineData("/data/cache", "/data/cache")]
    public void Resolve_LongestPrefixWins(string request, string expectedMountPath)
    {
        var mounts = AggregateRouter.SortByLongestPrefix(new[]
        {
            Mount("/data"),
            Mount("/data/cache"),
        });

        var resolved = AggregateRouter.Resolve(mounts, request);

        Assert.NotNull(resolved);
        Assert.Equal(expectedMountPath, resolved!.MountPath.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [AggregateRouter] - Resolve: unrelated path returns null")]
    public void Resolve_Unrelated_ReturnsNull()
    {
        var mounts = AggregateRouter.SortByLongestPrefix(new[] { Mount("/data") });
        Assert.Null(AggregateRouter.Resolve(mounts, "/unrelated"));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateRouter] - Resolve: prefix match requires segment boundary")]
    public void Resolve_SegmentBoundary()
    {
        // "/data" must not match "/database".
        var mounts = AggregateRouter.SortByLongestPrefix(new[] { Mount("/data") });
        Assert.Null(AggregateRouter.Resolve(mounts, "/database"));
        Assert.NotNull(AggregateRouter.Resolve(mounts, "/data/foo"));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateRouter] - Resolve: root mount matches everything")]
    public void Resolve_RootMount_MatchesEverything()
    {
        var mounts = AggregateRouter.SortByLongestPrefix(new[] { Mount("/") });
        Assert.NotNull(AggregateRouter.Resolve(mounts, "/anything"));
        Assert.NotNull(AggregateRouter.Resolve(mounts, "/a/b/c"));
        Assert.NotNull(AggregateRouter.Resolve(mounts, "/"));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateRouter] - IsSyntheticIntermediate: detects mount ancestors")]
    public void IsSyntheticIntermediate_DetectsAncestors()
    {
        var mounts = AggregateRouter.SortByLongestPrefix(new[] { Mount("/data/cache") });

        Assert.True(AggregateRouter.IsSyntheticIntermediate(mounts, "/data"));
        Assert.True(AggregateRouter.IsSyntheticIntermediate(mounts, "/"));
        Assert.False(AggregateRouter.IsSyntheticIntermediate(mounts, "/unrelated"));
        // The mount root itself is NOT a synthetic intermediate — it's a real mount.
        Assert.False(AggregateRouter.IsSyntheticIntermediate(mounts, "/data/cache"));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateRouter] - SyntheticChildren: returns one-segment-down children")]
    public void SyntheticChildren_ReturnsImmediateChildren()
    {
        var mounts = AggregateRouter.SortByLongestPrefix(new[]
        {
            Mount("/data/cache"),
            Mount("/data/store"),
            Mount("/var/log"),
        });

        var rootChildren = AggregateRouter.SyntheticChildren(mounts, "/");
        Assert.Contains("data", rootChildren);
        Assert.Contains("var", rootChildren);
        Assert.Equal(2, rootChildren.Count);

        var dataChildren = AggregateRouter.SyntheticChildren(mounts, "/data");
        Assert.Contains("cache", dataChildren);
        Assert.Contains("store", dataChildren);
        Assert.Equal(2, dataChildren.Count);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateRouter] - Mount: normalizes path (rooted, no trailing slash)")]
    public void Mount_NormalizesPath()
    {
        var explicitRooted = Mount("/data");
        var implicitRelative = Mount("data");
        var trailingSlash = Mount("/data/");

        Assert.Equal("/data", explicitRooted.MountPath.ToString());
        Assert.Equal("/data", implicitRelative.MountPath.ToString());
        Assert.Equal("/data", trailingSlash.MountPath.ToString());
    }

    [Theory(DisplayName = "Cohesion Test [AggregateRouter] - Mount.ToProviderPath strips mount prefix")]
    [InlineData("/data", "/data/foo.txt", "/foo.txt")]
    [InlineData("/data", "/data", "/")]
    [InlineData("/data/cache", "/data/cache/hot.bin", "/hot.bin")]
    [InlineData("/", "/anywhere", "/anywhere")]
    public void Mount_ToProviderPath(string mountPath, string requested, string expectedProviderPath)
    {
        var mount = Mount(mountPath);
        Assert.Equal(expectedProviderPath, mount.ToProviderPath(requested).ToString());
    }

    [Theory(DisplayName = "Cohesion Test [AggregateRouter] - Mount.ToAggregatePath prepends mount prefix")]
    [InlineData("/data", "/foo.txt", "/data/foo.txt")]
    [InlineData("/data", "/", "/data")]
    [InlineData("/", "/foo", "/foo")]
    public void Mount_ToAggregatePath(string mountPath, string providerPath, string expectedAggregatePath)
    {
        var mount = Mount(mountPath);
        Assert.Equal(expectedAggregatePath, mount.ToAggregatePath(providerPath).ToString());
    }
}
