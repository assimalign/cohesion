using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.SourceGeneration.Web.Tests;

/// <summary>
/// GeneratorDriver-level coverage for <see cref="EndpointBindingGenerator"/>: each typed <c>Map*</c>
/// call site is run through the generator and the emitted interceptor is asserted for shape.
/// </summary>
public class EndpointBindingGeneratorTests
{
    private const string Preamble = """
        using System.Threading.Tasks;
        using Assimalign.Cohesion.Http;
        using Assimalign.Cohesion.ObjectValidation;
        using Assimalign.Cohesion.Web;
        using Assimalign.Cohesion.Web.Hosting;

        public sealed class Widget { public string Name { get; set; } = ""; public int Quantity { get; set; } }
        """;

    private static string Run(string body)
    {
        string source = Preamble + "\n\npublic static class Endpoints\n{\n    public static void Configure(WebApplication app, IValidator validator)\n    {\n" + body + "\n    }\n}\n";

        List<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(Assimalign.Cohesion.Http.IHttpContext).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Assimalign.Cohesion.Web.IWebApplicationPipelineBuilder).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Assimalign.Cohesion.Web.WebApplicationPipelineBuilderExtensions).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Assimalign.Cohesion.Web.Hosting.WebApplication).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Assimalign.Cohesion.Web.Routing.RouteValueDictionary).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Assimalign.Cohesion.ObjectValidation.IValidator).Assembly.Location))
            .ToList();

        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);

        CSharpCompilation compilation = CSharpCompilation.Create(
            "EndpointBindingGeneratorTests",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EndpointBindingGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        GeneratorDriverRunResult runResult = driver.GetRunResult();

        return runResult.GeneratedTrees.Length > 0 ? runResult.GeneratedTrees[0].ToString() : string.Empty;
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: route parameter emits an interceptor")]
    public void Generator_RouteParameter_EmitsInterceptor()
    {
        string generated = Run("""app.MapGet("/users/{id}", async (int id, IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldContain("Intercept_0", Case.Sensitive);
        generated.ShouldContain("(global::System.Func<global::System.Int32, global::Assimalign.Cohesion.Http.IHttpContext, global::System.Threading.Tasks.Task>)handler", Case.Sensitive);
        generated.ShouldContain("context.TryGetRouteValues(out var __routeValues0)", Case.Sensitive);
        generated.ShouldContain("__routeValues0.TryGetValue(\"id\"", Case.Sensitive);
        generated.ShouldContain("__handler(__arg0, __arg1)", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: query scalar infers query source and 400 on failure")]
    public void Generator_QueryScalar_EmitsQueryBindingWithBadRequest()
    {
        string generated = Run("""app.MapGet("/search", async (string q, int page, IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldContain("context.Request.Query.TryGetValue(\"q\"", Case.Sensitive);
        generated.ShouldContain("context.Request.Query.TryGetValue(\"page\"", Case.Sensitive);
        generated.ShouldContain("global::System.Int32.TryParse(__raw1", Case.Sensitive);
        generated.ShouldContain("HttpStatusCode.BadRequest", Case.Sensitive);
        generated.ShouldContain("\"errors\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: complex parameter binds from body with 415 and 400 semantics")]
    public void Generator_ComplexParameter_EmitsBodyBinding()
    {
        string generated = Run("""app.MapPost("/widgets", async (Widget widget, IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldContain("ReadContentAsync<global::Widget>", Case.Sensitive);
        generated.ShouldContain("HttpStatusCode.UnsupportedMediaType", Case.Sensitive);
        generated.ShouldContain("catch (global::System.Text.Json.JsonException)", Case.Sensitive);
        generated.ShouldContain("using Assimalign.Cohesion.Web.Serialization;", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: explicit header attribute overrides inference")]
    public void Generator_HeaderAttribute_EmitsHeaderBinding()
    {
        string generated = Run("""app.MapGet("/whoami", async ([FromHeader(Name = "X-User")] string user, IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldContain("context.Request.Headers.GetValue(\"X-User\")", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: validator overload attaches metadata and runs validation")]
    public void Generator_ValidatorOverload_EmitsValidationSeam()
    {
        string generated = Run("""app.MapPost("/widgets", async (Widget widget, IHttpContext context) => { await Task.CompletedTask; }, validator);""");

        generated.ShouldContain("global::Assimalign.Cohesion.Web.EndpointValidationMetadata(validator)", Case.Sensitive);
        generated.ShouldContain("validator.Validate(__arg0)", Case.Sensitive);
        generated.ShouldContain("EndpointValidation.WriteProblemAsync", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: injections bind context and cancellation directly")]
    public void Generator_Injections_EmitDirectBinding()
    {
        string generated = Run("""app.MapGet("/inject", async (IHttpContext context, System.Threading.CancellationToken token) => { await Task.CompletedTask; });""");

        generated.ShouldContain("__arg0 = context;", Case.Sensitive);
        generated.ShouldContain("__arg1 = context.RequestCancelled;", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: single-context handler is left to the middleware overload")]
    public void Generator_SingleContextHandler_IsNotIntercepted()
    {
        string generated = Run("""app.MapGet("/raw", async (IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldNotContain("Intercept_", Case.Sensitive);
    }
}
