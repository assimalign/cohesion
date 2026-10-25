using System;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceConfigurationSectionTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Resources] - Configuration section: ";

    [Fact(DisplayName = DisplayPrefix + "The schema comes from the source-generated metadata, with UI titles and descriptions")]
    public void Create_WithAnnotatedOptions_GeneratesAnAnnotatedSchema()
    {
        // Act
        ResourceConfigurationSection section = ResourceConfigurationSection.Create(
            "Test:Policy",
            TestSurfaceJsonContext.Default.TestPolicyOptions,
            ResourceConfigurationMutability.Live,
            new TestPolicyOptions(),
            static _ => null);

        // Assert
        JsonElement schema = section.Schema;
        schema.GetProperty("title").GetString().ShouldBe("Origins policy");
        schema.GetProperty("description").GetString().ShouldBe("Which origins may call the resource.");
        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        JsonElement origins = schema.GetProperty("properties").GetProperty("origins");
        origins.GetProperty("title").GetString().ShouldBe("Allowed origins");
        origins.GetProperty("type").GetString().ShouldBe("array");
        section.Path.Value.ShouldBe("Test:Policy");
        section.Mutability.ShouldBe(ResourceConfigurationMutability.Live);
        section.Scope.ShouldBe(ResourceConfigurationScope.Process);
        section.Claimable.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "A claimable section must bind to a JSON object of entries")]
    public void Create_ClaimableNonObject_ShouldThrow()
    {
        // Act
        Should.Throw<InvalidOperationException>(() => ResourceConfigurationSection.Create(
            "Test:Name",
            TestSurfaceJsonContext.Default.String,
            ResourceConfigurationMutability.Live,
            "baseline",
            static _ => null,
            ResourceConfigurationScope.Backend,
            claimable: true));
    }

    [Fact(DisplayName = DisplayPrefix + "A claimable dictionary section is accepted")]
    public void Create_ClaimableDictionary_ShouldSucceed()
    {
        // Act
        ResourceConfigurationSection section = ResourceConfigurationSection.Create(
            "Test:Databases",
            TestSurfaceJsonContext.Default.DictionaryStringTestDatabaseEntry,
            ResourceConfigurationMutability.Live,
            new Dictionary<string, TestDatabaseEntry>(),
            static _ => null,
            ResourceConfigurationScope.Backend,
            claimable: true);

        // Assert
        section.Claimable.ShouldBeTrue();
        section.Scope.ShouldBe(ResourceConfigurationScope.Backend);
    }

    [Fact(DisplayName = DisplayPrefix + "A default path is refused")]
    public void Create_DefaultPath_ShouldThrow()
    {
        // Act
        Should.Throw<ArgumentException>(() => ResourceConfigurationSection.Create(
            default,
            TestSurfaceJsonContext.Default.TestLimitsOptions,
            ResourceConfigurationMutability.Restart,
            new TestLimitsOptions(),
            static _ => null));
    }

    [Theory(DisplayName = DisplayPrefix + "The path grammar accepts colon-separated configuration segments only")]
    [InlineData("Web:Cors", true)]
    [InlineData("Web", true)]
    [InlineData("Web:Http2.Limits_x-y", true)]
    [InlineData(":Web", false)]
    [InlineData("Web:", false)]
    [InlineData("Web::Cors", false)]
    [InlineData("Web Cors", false)]
    [InlineData("", false)]
    public void IsValid_ConfigurationPath_FollowsTheGrammar(string value, bool expected)
    {
        // Act
        bool valid = ResourceConfigurationPath.IsValid(value, out string? message);

        // Assert
        valid.ShouldBe(expected);
        (message is null).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "Action and tool names are lowercase hyphen-separated words")]
    [InlineData("flush-cache", true)]
    [InlineData("sql", true)]
    [InlineData("backup2", true)]
    [InlineData("Flush", false)]
    [InlineData("-flush", false)]
    [InlineData("flush--cache", false)]
    [InlineData("flush_cache", false)]
    public void IsValid_ActionAndToolNames_FollowTheGrammar(string value, bool expected)
    {
        // Act
        bool action = ResourceActionName.IsValid(value, out _);
        bool tool = ResourceToolName.IsValid(value, out _);

        // Assert
        action.ShouldBe(expected);
        tool.ShouldBe(expected);
    }
}
