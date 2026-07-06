using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlTextFacadeTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Facade: stream parsing detects UTF-16 input")]
    public void Parse_Utf16Stream_Detected()
    {
        var bytes = Concat(
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetPreamble(),
            new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("key: value"));
        using var stream = new MemoryStream(bytes);

        var document = YamlText.Parse(stream)[0];

        ((YamlScalar)((YamlMapping)document.Root!)["key"]).Value.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Facade: ParseDocument rejects multi-document input")]
    public void ParseDocument_MultipleDocuments_Throws()
    {
        Should.Throw<YamlException>(() => YamlText.ParseDocument("---\na: 1\n---\nb: 2"));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Facade: the content reader and writer seams round-trip")]
    public void ReaderWriterSeams_RoundTrip()
    {
        var reader = YamlText.CreateReader();
        var writer = YamlText.CreateWriter();

        using var input = new MemoryStream(Encoding.UTF8.GetBytes("key: value\n"));
        var stream = reader.Read(input);

        using var output = new MemoryStream();
        writer.Write(output, stream);

        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("key: value\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Facade: the format descriptor names the specification")]
    public void Format_Descriptor_IsComplete()
    {
        YamlText.Format.Name.ShouldBe("YAML");
        YamlText.Format.Kind.ShouldBe(ContentKind.Document);
        YamlText.Format.MediaTypes.ShouldContain("application/yaml");
        YamlText.Format.FileExtensions.ShouldContain(".yaml");
        YamlText.Format.Specification.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Facade: YamlException is a ContentFormatException")]
    public void YamlException_IsContentFormatException()
    {
        var exception = Should.Throw<YamlException>(() => YamlText.Parse("[1, 2"));

        exception.ShouldBeAssignableTo<ContentFormatException>();
        exception.Line.ShouldBeGreaterThan(0);
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}
