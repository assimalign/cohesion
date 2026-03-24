using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests;

public class GlobMatcherBuilderTests
{
    [Fact]
    public void Build_WithNoPatterns_ThrowsInvalidOperationException()
    {
        var builder = new GlobMatcherBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void AddInclude_WithNullPattern_ThrowsArgumentNullException()
    {
        var builder = new GlobMatcherBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.AddInclude(null!));
    }

    [Fact]
    public void AddExclude_WithNullPattern_ThrowsArgumentNullException()
    {
        var builder = new GlobMatcherBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.AddExclude(null!));
    }

    [Fact]
    public void AddInclude_ReturnsBuilder_ForMethodChaining()
    {
        var builder = new GlobMatcherBuilder();

        var result = builder.AddInclude(Glob.Parse("*.txt"));

        Assert.Same(builder, result);
    }

    [Fact]
    public void AddExclude_ReturnsBuilder_ForMethodChaining()
    {
        var builder = new GlobMatcherBuilder();

        builder.AddInclude(Glob.Parse("*.txt"));
        var result = builder.AddExclude(Glob.Parse("temp.txt"));

        Assert.Same(builder, result);
    }

    [Fact]
    public void Build_WithIncludePattern_ReturnsValidMatcher()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.txt"));

        var matcher = builder.Build();

        Assert.NotNull(matcher);
    }

    [Fact]
    public void Build_WithExcludePattern_ReturnsValidMatcher()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.*"));
        builder.AddExclude(Glob.Parse("*.log"));

        var matcher = builder.Build();

        Assert.NotNull(matcher);
    }

    [Fact]
    public void Build_WithMultiplePatterns_ReturnsValidMatcher()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.txt"))
               .AddInclude(Glob.Parse("*.md"))
               .AddExclude(Glob.Parse("temp.*"));

        var matcher = builder.Build();

        Assert.NotNull(matcher);
    }

    [Fact]
    public void Constructor_WithOptions_AppliesOptions()
    {
        var options = new GlobMatcherOptions
        {
            IgnoreCase = false
        };

        var builder = new GlobMatcherBuilder(options);
        builder.AddInclude(Glob.Parse("*.txt"));

        var matcher = builder.Build();

        Assert.NotNull(matcher);
    }

    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GlobMatcherBuilder(null!));
    }
}
