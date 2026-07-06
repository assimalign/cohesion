using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Tests;

public class ContentFormatTests
{
    [Fact(DisplayName = "Cohesion Test [Content] - Format: descriptor carries name, kind, and associations")]
    public void ContentFormat_Descriptor_CarriesMetadata()
    {
        var format = new ContentFormat
        {
            Name = "YAML",
            Kind = ContentKind.Document,
            MediaTypes = ["application/yaml", "text/yaml"],
            FileExtensions = [".yaml", ".yml"],
            Specification = "https://yaml.org/spec/1.2.2/"
        };

        format.Name.ShouldBe("YAML");
        format.Kind.ShouldBe(ContentKind.Document);
        format.MediaTypes.ShouldBe(["application/yaml", "text/yaml"]);
        format.FileExtensions.ShouldBe([".yaml", ".yml"]);
        format.Specification.ShouldBe("https://yaml.org/spec/1.2.2/");
        format.ToString().ShouldBe("YAML");
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Format: the unknown descriptor is a shared default")]
    public void ContentFormat_Unknown_IsSharedDefault()
    {
        ContentFormat.Unknown.ShouldBeSameAs(ContentFormat.Unknown);
        ContentFormat.Unknown.Kind.ShouldBe(ContentKind.Unknown);

        using var content = ContentFactory.FromBytes(new byte[] { 1 });
        content.Format.ShouldBeSameAs(ContentFormat.Unknown);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Format: format exception carries the failure position")]
    public void ContentFormatException_Position_IsPreserved()
    {
        var exception = new ContentFormatException("Unexpected token.", position: 42);

        exception.Position.ShouldBe(42);
        exception.ShouldBeAssignableTo<ContentException>();
    }
}
