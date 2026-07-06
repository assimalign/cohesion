using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Serialization.Tests;

public class OpenApiYamlSerializationTests
{
    private const string PetstoreYaml = """
        openapi: 3.1.2
        info:
          title: Petstore
          version: 1.0.0
        paths:
          /pets/{id}:
            get:
              operationId: getPet
              parameters:
                - name: id
                  in: path
                  required: true
                  schema:
                    type: integer
              responses:
                "200":
                  description: A single pet.
                  content:
                    application/json:
                      schema:
                        $ref: "#/components/schemas/Pet"
        components:
          schemas:
            Pet:
              type: object
              required:
                - name
              properties:
                name:
                  type: string
                tag:
                  type: [string, "null"]
        """;

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: parses a representative document")]
    public void Parse_RepresentativeDocument_MapsToModel()
    {
        var document = OpenApiYaml.Parse(PetstoreYaml);

        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
        document.Info.Title.ShouldBe("Petstore");

        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.OperationId.ShouldBe("getPet");
        operation.Parameters[0].Schema!.Type.ShouldBe(SchemaType.Integer);
        operation.Responses!.Items["200"].Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/Pet");

        var pet = document.Components!.Schemas["Pet"];
        pet.Properties["tag"].Type.ShouldBe(SchemaType.String);
        pet.Properties["tag"].Nullable.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: equivalent JSON and YAML produce the same model")]
    public void Parse_EquivalentJsonAndYaml_SameModel()
    {
        var fromYaml = OpenApiYaml.Parse(PetstoreYaml);
        var fromJson = OpenApiJson.Parse(fromYaml.ToJson(indented: false));

        fromJson.ToJson(indented: false).ShouldBe(fromYaml.ToJson(indented: false));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: write-parse-write round-trips are stable")]
    public void Write_RoundTrip_IsStable()
    {
        var document = OpenApiYaml.Parse(PetstoreYaml);

        var first = document.ToYaml();
        var second = OpenApiYaml.Parse(first).ToYaml();

        second.ShouldBe(first);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: references and extensions survive the YAML round-trip")]
    public void Write_ReferencesAndExtensions_Preserved()
    {
        var document = OpenApiYaml.Parse("""
            openapi: 3.1.2
            info:
              title: t
              version: "1"
            x-vendor:
              nested: [1, 2]
            paths:
              /a:
                $ref: "external.yaml#/paths/~1a"
            components:
              schemas:
                Local:
                  $ref: "#/components/schemas/Other"
                Other:
                  type: string
            """);

        var yaml = document.ToYaml();
        var reparsed = OpenApiYaml.Parse(yaml);

        reparsed.Extensions["x-vendor"].ShouldBeOfType<OpenApiObjectNode>();
        reparsed.Paths!.Items["/a"].Reference!.Ref.ShouldBe("external.yaml#/paths/~1a");
        reparsed.Components!.Schemas["Local"].Reference!.Ref.ShouldBe("#/components/schemas/Other");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: version gating applies to YAML output")]
    public void Write_VersionGating_AppliesToYaml()
    {
        var document = OpenApiYaml.Parse(PetstoreYaml);
        document.Webhooks["onPet"] = new OpenApiPathItem();

        document.ToYaml(OpenApiSpecVersion.V3_1).ShouldContain("webhooks:", Case.Sensitive);
        document.ToYaml(OpenApiSpecVersion.V3_0).ShouldNotContain("webhooks:", Case.Sensitive);
        document.ToYaml(OpenApiSpecVersion.V3_0).ShouldContain("openapi: 3.0.4", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: malformed YAML surfaces as OpenApiException")]
    public void Parse_MalformedYaml_ThrowsOpenApiException()
    {
        var exception = Should.Throw<OpenApiException>(() => OpenApiYaml.Parse("info: [unclosed"));

        exception.Message.ShouldContain("not valid YAML");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: multi-document input is rejected")]
    public void Parse_MultiDocumentInput_Throws()
    {
        Should.Throw<OpenApiException>(() => OpenApiYaml.Parse("---\nopenapi: 3.1.2\n---\nopenapi: 3.0.4"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: stream parsing detects the encoding")]
    public void Parse_Utf16Stream_Detected()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        using var stream = new MemoryStream(encoding.GetPreamble().Length + 64);
        var bytes = encoding.GetPreamble();
        stream.Write(bytes);
        stream.Write(new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("openapi: 3.1.2\ninfo: {title: t, version: \"1\"}"));
        stream.Position = 0;

        var document = OpenApiYaml.Parse(stream);

        document.Info.Title.ShouldBe("t");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Yaml: scalar kinds survive the format boundary")]
    public void Parse_ScalarKinds_Preserved()
    {
        var document = OpenApiYaml.Parse("""
            openapi: 3.1.2
            info:
              title: t
              version: "1"
            x-values:
              integer: 42
              number: 3.5
              flag: true
              nothing: null
              text: "42"
            """);

        var values = (OpenApiObjectNode)document.Extensions["x-values"];
        ((OpenApiValueNode)values["integer"]).Kind.ShouldBe(OpenApiValueKind.Integer);
        ((OpenApiValueNode)values["number"]).Kind.ShouldBe(OpenApiValueKind.Double);
        ((OpenApiValueNode)values["flag"]).Kind.ShouldBe(OpenApiValueKind.Boolean);
        ((OpenApiValueNode)values["nothing"]).Kind.ShouldBe(OpenApiValueKind.Null);
        ((OpenApiValueNode)values["text"]).Kind.ShouldBe(OpenApiValueKind.String);

        // And back out through YAML: the string "42" stays quoted, the integer stays plain.
        var yaml = document.ToYaml();
        yaml.ShouldContain("integer: 42", Case.Sensitive);
        yaml.ShouldContain("text: \"42\"", Case.Sensitive);
    }
}
