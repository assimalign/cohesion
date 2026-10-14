using System.Linq;

using Microsoft.CodeAnalysis;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.SourceGeneration.ObjectMapping.Tests;

/// <summary>Preserves the mapper generator's existing output and fallback behavior through its area move.</summary>
public sealed class MapperProfileGeneratorTests
{
    private const string models = """
        using Assimalign.Cohesion.ObjectMapping;

        namespace MappingConsumer;

        public sealed class Source { public string Name { get; set; } = ""; }
        public sealed class Target { public string Name { get; set; } = ""; }

        """;

    [Fact(DisplayName = "Cohesion Test [ObjectMapping.SourceGeneration] - Generate: partial profiles retain their generated override")]
    public void Generate_PartialScalarProfile_EmitsCompilableOverride()
    {
        // Arrange
        var source = models + """
            public partial class Profile : MapperProfile<Target, Source>
            {
                protected override void Configure(MapperProfileDescriptor<Target, Source> descriptor)
                    => descriptor.MapMember(target => target.Name, source => source.Name);
            }
            """;

        // Act
        var run = MapperGeneratorHarness.Run(source);

        // Assert
        run.Result.Diagnostics.ShouldBeEmpty();
        run.Errors.ShouldBeEmpty();
        var generated = run.Result.Results.Single().GeneratedSources.ShouldHaveSingleItem();
        generated.HintName.ShouldBe("Profile.MapperProfile.g.cs");
        generated.SourceText.ToString().ShouldContain(
            ".MapMember(source => source.Name, static (target, value) => target.Name = value)", Case.Sensitive);
        var profile = run.Output.GetTypeByMetadataName("MappingConsumer.Profile");
        profile.ShouldNotBeNull();
        profile.GetMembers("TryConfigureGenerated").OfType<IMethodSymbol>().ShouldHaveSingleItem().IsOverride.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping.SourceGeneration] - Generate: inline profiles retain their interceptor namespace")]
    public void Generate_InlineScalarProfile_EmitsCompilableInterceptor()
    {
        // Arrange
        var source = models + """
            public static class Configuration
            {
                public static MapperBuilder Configure(MapperBuilder builder)
                    => builder.AddProfile<Target, Source>(descriptor => descriptor
                        .MapMember(target => target.Name, source => source.Name));
            }
            """;

        // Act
        var run = MapperGeneratorHarness.Run(source);

        // Assert
        run.Result.Diagnostics.ShouldBeEmpty();
        run.Errors.ShouldBeEmpty();
        var generated = run.Result.Results.Single().GeneratedSources.ShouldHaveSingleItem();
        generated.HintName.ShouldBe("MapperInlineInterceptors.g.cs");
        generated.SourceText.ToString().ShouldContain("namespace Assimalign.Cohesion.ObjectMapping.Generated", Case.Sensitive);
        generated.SourceText.ToString().ShouldContain("InterceptsLocation", Case.Sensitive);
        generated.SourceText.ToString().ShouldContain("=> builder.AddProfile(new __InlineMapperProfile_0());", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping.SourceGeneration] - Generate: a multi-statement profile keeps its reflection fallback")]
    public void Generate_MultiStatementProfile_LeavesFallbackUnchanged()
    {
        // Arrange
        var source = models + """
            public partial class Profile : MapperProfile<Target, Source>
            {
                protected override void Configure(MapperProfileDescriptor<Target, Source> descriptor)
                {
                    descriptor.MapMember(target => target.Name, source => source.Name);
                    descriptor.MapMember(target => target.Name, source => source.Name);
                }
            }
            """;

        // Act
        var run = MapperGeneratorHarness.Run(source);

        // Assert
        run.Result.Diagnostics.ShouldBeEmpty();
        run.Errors.ShouldBeEmpty();
        run.Result.GeneratedTrees.ShouldBeEmpty();
        run.Output.GetTypeByMetadataName("MappingConsumer.Profile")!.GetMembers("TryConfigureGenerated").ShouldBeEmpty();
    }
}
