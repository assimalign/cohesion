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
        using Assimalign.Cohesion.Web;
        using Assimalign.Cohesion.Web.Hosting;

        public sealed class Widget { public string Name { get; set; } = ""; public int Quantity { get; set; } }
        """;

    // The antiforgery requirement the generator attaches to a form-bound endpoint when the application
    // references Web.Antiforgery.
    private const string antiforgeryRequirement = ".WithMetadata(global::Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata.Required);";

    private static string Run(string body, bool referenceAntiforgery = false)
        => Generate(body, referenceAntiforgery, out _);

    private static string Generate(string body, bool referenceAntiforgery, out Compilation output)
    {
        string source = Preamble + "\n\npublic static class Endpoints\n{\n    public static void Configure(WebApplication app)\n    {\n" + body + "\n    }\n}\n";

        string antiforgeryAssembly = typeof(Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata).Assembly.Location;

        // The test host's trusted platform assemblies include every assembly this project references,
        // Web.Antiforgery among them. Exclude it unless the case models an application that references
        // it, and add every Cohesion assembly once.
        List<string> paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Append(typeof(Assimalign.Cohesion.Http.IHttpContext).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.IWebApplicationPipelineBuilder).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.WebApplicationPipelineBuilderExtensions).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.Hosting.WebApplication).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.Routing.RouteValueDictionary).Assembly.Location)
            .Where(path => referenceAntiforgery || !IsSameFile(path, antiforgeryAssembly))
            .Append(referenceAntiforgery ? antiforgeryAssembly : string.Empty)
            .Where(path => path.Length > 0)
            .DistinctBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<MetadataReference> references = paths
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        CSharpParseOptions parseOptions = new CSharpParseOptions(LanguageVersion.Preview)
            .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Assimalign.Cohesion.Web.Api.Generated")]);

        CSharpCompilation compilation = CSharpCompilation.Create(
            "EndpointBindingGeneratorTests",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EndpointBindingGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out _);

        GeneratorDriverRunResult runResult = driver.GetRunResult();

        return runResult.GeneratedTrees.Length > 0 ? runResult.GeneratedTrees[0].ToString() : string.Empty;
    }

    private static bool IsSameFile(string path, string other)
        => string.Equals(Path.GetFileName(path), Path.GetFileName(other), StringComparison.OrdinalIgnoreCase);

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

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: interceptors return the mapped route's builder")]
    public void Generator_Interceptor_ReturnsRouteBuilder()
    {
        string generated = Run("""app.MapGet("/users/{id}", async (int id, IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldContain("public static global::Assimalign.Cohesion.Web.Routing.IRouterRouteBuilder Intercept_0(", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a route-group endpoint binds names outside its template from route or query")]
    public void Generator_GroupEndpoint_BindsUnnamedParametersFromRouteOrQuery()
    {
        string generated = Run("""app.MapGroup("api/{tenant}").MapGet("orders/{id}", async (string tenant, int id, IHttpContext context) => { await Task.CompletedTask; });""");

        // The group receiver is intercepted like the application's.
        generated.ShouldContain("(this global::Assimalign.Cohesion.Web.Routing.IRouterGroupBuilder builder", Case.Sensitive);

        // 'id' is in the visible template: a plain route read. 'tenant' comes from the group prefix,
        // which the call site cannot see: route values first, then the query string.
        generated.ShouldContain("__routeValues1.TryGetValue(\"id\"", Case.Sensitive);
        generated.ShouldNotContain("context.Request.Query.TryGetValue(\"id\"", Case.Sensitive);
        generated.ShouldContain("__routeValues0.TryGetValue(\"tenant\"", Case.Sensitive);
        generated.ShouldContain("if (__raw0 is null && context.Request.Query.TryGetValue(\"tenant\", out var __query0))", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: an application endpoint with a literal template keeps query inference")]
    public void Generator_ApplicationEndpoint_LiteralTemplate_KeepsQueryInference()
    {
        string generated = Run("""app.MapGet("/search", async (string q, IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldContain("context.Request.Query.TryGetValue(\"q\", out var __query0) ? __query0.Value : null", Case.Sensitive);
        generated.ShouldNotContain("__routeValues0", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a form-bound endpoint requires antiforgery when the application references Web.Antiforgery")]
    public void Generator_FormEndpointWithAntiforgeryReferenced_AttachesAntiforgeryRequirement()
    {
        // Act
        string generated = Run(
            """app.MapPost("/orders", async ([FromForm] string title, IHttpContext context) => { await Task.CompletedTask; });""",
            referenceAntiforgery: true);

        // Assert — the requirement is chained onto the route the raw Map overload returns.
        generated.ShouldContain("await context.ReadFormAsync(context.RequestCancelled);", Case.Sensitive);
        generated.ShouldContain("            })" + antiforgeryRequirement, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a form-bound endpoint carries no antiforgery requirement without Web.Antiforgery")]
    public void Generator_FormEndpointWithoutAntiforgeryReferenced_EmitsNoRequirement()
    {
        // Act
        string generated = Run(
            """app.MapPost("/orders", async ([FromForm] string title, IHttpContext context) => { await Task.CompletedTask; });""",
            referenceAntiforgery: false);

        // Assert — the endpoint is still intercepted and bound; it simply requires nothing.
        generated.ShouldContain("Intercept_0", Case.Sensitive);
        generated.ShouldContain("await context.ReadFormAsync(context.RequestCancelled);", Case.Sensitive);
        generated.ShouldNotContain("Antiforgery", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: an endpoint without form parameters carries no antiforgery requirement")]
    public void Generator_NonFormEndpointWithAntiforgeryReferenced_EmitsNoRequirement()
    {
        // Act — query and body binding with the package referenced.
        string generated = Run(
            """
            app.MapPost("/search", async (string q, IHttpContext context) => { await Task.CompletedTask; });
            app.MapPost("/widgets", async (Widget widget, IHttpContext context) => { await Task.CompletedTask; });
            """,
            referenceAntiforgery: true);

        // Assert
        generated.ShouldContain("Intercept_0", Case.Sensitive);
        generated.ShouldContain("Intercept_1", Case.Sensitive);
        generated.ShouldNotContain("Antiforgery", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: only the form-bound endpoint among several requires antiforgery")]
    public void Generator_MixedEndpoints_AttachesRequirementOnlyToFormEndpoint()
    {
        // Act
        string generated = Run(
            """
            app.MapGet("/orders/{id}", async (int id, IHttpContext context) => { await Task.CompletedTask; });
            app.MapPost("/orders", async ([FromForm] string title, [FromForm(Name = "qty")] int quantity, IHttpContext context) => { await Task.CompletedTask; });
            """,
            referenceAntiforgery: true);

        // Assert — one requirement, on the second interceptor.
        int first = generated.IndexOf("Intercept_0(", StringComparison.Ordinal);
        int second = generated.IndexOf("Intercept_1(", StringComparison.Ordinal);
        int requirement = generated.IndexOf(antiforgeryRequirement, StringComparison.Ordinal);

        first.ShouldBeGreaterThanOrEqualTo(0);
        second.ShouldBeGreaterThan(first);
        requirement.ShouldBeGreaterThan(second);
        generated.LastIndexOf(antiforgeryRequirement, StringComparison.Ordinal).ShouldBe(requirement);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a form-bound route-group endpoint requires antiforgery too")]
    public void Generator_FormGroupEndpointWithAntiforgeryReferenced_AttachesAntiforgeryRequirement()
    {
        // Act
        string generated = Run(
            """app.MapGroup("forms").MapPost("contact", async ([FromForm] string email, IHttpContext context) => { await Task.CompletedTask; });""",
            referenceAntiforgery: true);

        // Assert
        generated.ShouldContain("(this global::Assimalign.Cohesion.Web.Routing.IRouterGroupBuilder builder", Case.Sensitive);
        generated.ShouldContain(antiforgeryRequirement, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: the attached antiforgery requirement compiles against Web.Antiforgery")]
    public void Generator_FormEndpointWithAntiforgeryReferenced_GeneratedCodeCompiles()
    {
        // Act — the caller's own opt-out chains after the generated requirement.
        string generated = Generate(
            """app.MapPost("/orders", async ([FromForm] string title, IHttpContext context) => { await Task.CompletedTask; }).WithMetadata(Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata.Disabled);""",
            referenceAntiforgery: true,
            out Compilation output);

        // Assert — the interceptor (requirement included) is part of a compilation with no errors.
        Diagnostic[] errors = output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        generated.ShouldContain(antiforgeryRequirement, Case.Sensitive);
        output.SyntaxTrees.Count().ShouldBe(2);
        errors.ShouldBeEmpty(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }
}
