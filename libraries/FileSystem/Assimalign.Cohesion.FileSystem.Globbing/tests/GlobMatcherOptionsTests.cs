using System;
using System.Globalization;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests;

public class GlobMatcherOptionsTests
{
    [Fact]
    public void Constructor_DefaultIgnoreCase_IsTrue()
    {
        var options = new GlobMatcherOptions();

        Assert.True(options.IgnoreCase);
    }

    [Fact]
    public void Constructor_DefaultCultureInfo_IsInvariantCulture()
    {
        var options = new GlobMatcherOptions();

        Assert.Equal(CultureInfo.InvariantCulture, options.CultureInfo);
    }

    [Fact]
    public void Constructor_DefaultExcludeDirectories_IsFalse()
    {
        var options = new GlobMatcherOptions();

        Assert.False(options.ExcludeDirectories);
    }

    [Fact]
    public void IgnoreCase_CanBeSet()
    {
        var options = new GlobMatcherOptions
        {
            IgnoreCase = false
        };

        Assert.False(options.IgnoreCase);
    }

    [Fact]
    public void CultureInfo_CanBeSet()
    {
        var culture = new CultureInfo("en-US");
        var options = new GlobMatcherOptions
        {
            CultureInfo = culture
        };

        Assert.Equal(culture, options.CultureInfo);
    }

    [Fact]
    public void ExcludeDirectories_CanBeSet()
    {
        var options = new GlobMatcherOptions
        {
            ExcludeDirectories = true
        };

        Assert.True(options.ExcludeDirectories);
    }
}
