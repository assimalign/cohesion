using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.SourceGeneration.Web.Tests;

/// <summary>
/// GeneratorDriver-level coverage for <see cref="EndpointBindingGenerator"/>: each typed <c>Map*</c>
/// call site is run through the generator and the emitted interceptor, or the reported diagnostic, is
/// asserted for shape.
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

    /// <summary>The outcome of one generator run over a test source.</summary>
    /// <param name="Source">The test source the generator ran over.</param>
    /// <param name="Generated">The emitted interceptor source, or empty when nothing was emitted.</param>
    /// <param name="Diagnostics">The diagnostics the generator reported.</param>
    /// <param name="Output">The compilation with the generated source added.</param>
    private sealed record GeneratorRun(string Source, string Generated, ImmutableArray<Diagnostic> Diagnostics, Compilation Output)
    {
        /// <summary>Gets the compile errors of the output compilation, generated code included.</summary>
        public Diagnostic[] CompileErrors => Output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        /// <summary>Gets the reported diagnostics with the given id.</summary>
        public Diagnostic[] WithId(string id) => Diagnostics.Where(diagnostic => diagnostic.Id == id).ToArray();

        /// <summary>Gets the source text a diagnostic's location covers.</summary>
        public string TextAt(Diagnostic diagnostic) => Source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);
    }

    private static string Run(string body, bool referenceAntiforgery = false)
        => Generate(body, referenceAntiforgery: referenceAntiforgery).Generated;

    private static GeneratorRun Generate(
        string body,
        bool referenceAntiforgery = false,
        bool referenceSerialization = true,
        string members = "",
        string types = "")
    {
        string source = Preamble + "\n" + types + "\n\npublic static class Endpoints\n{\n" + members + "\n    public static void Configure(WebApplication app)\n    {\n" + body + "\n    }\n}\n";

        string antiforgeryAssembly = typeof(Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata).Assembly.Location;
        string serializationAssembly = typeof(Assimalign.Cohesion.Web.Serialization.IHttpContentSerializationFeature).Assembly.Location;

        // The test host's trusted platform assemblies include every assembly this project references,
        // Web.Antiforgery and Web.Serialization among them. Exclude each unless the case models an
        // application that references it, and add every Cohesion assembly once.
        List<string> paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Append(typeof(Assimalign.Cohesion.Http.IHttpContext).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.IWebApplicationPipelineBuilder).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.WebApplicationPipelineBuilderExtensions).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.Hosting.WebApplication).Assembly.Location)
            .Append(typeof(Assimalign.Cohesion.Web.Routing.RouteValueDictionary).Assembly.Location)
            .Where(path => referenceAntiforgery || !IsSameFile(path, antiforgeryAssembly))
            .Where(path => referenceSerialization || !IsSameFile(path, serializationAssembly))
            .Append(referenceAntiforgery ? antiforgeryAssembly : string.Empty)
            .Append(referenceSerialization ? serializationAssembly : string.Empty)
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
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions, path: "Endpoints.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EndpointBindingGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);

        GeneratorDriverRunResult runResult = driver.GetRunResult();
        string generated = runResult.GeneratedTrees.Length > 0 ? runResult.GeneratedTrees[0].ToString() : string.Empty;

        return new GeneratorRun(source, generated, diagnostics, output);
    }

    private static bool IsSameFile(string path, string other)
        => string.Equals(Path.GetFileName(path), Path.GetFileName(other), StringComparison.OrdinalIgnoreCase);

    private static string Describe(IEnumerable<Diagnostic> diagnostics)
        => string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.ToString()));

    // Every call in the source that binds a typed Map* placeholder (an overload taking System.Delegate)
    // must be rewritten by an interceptor in the output compilation; otherwise it would compile against
    // the placeholder, which throws when the endpoint is mapped.
    private static void AssertEveryTypedCallSiteIsIntercepted(GeneratorRun run)
    {
        SyntaxTree source = run.Output.SyntaxTrees.First();
        SemanticModel model = run.Output.GetSemanticModel(source);
        int typedCallSites = 0;

        foreach (InvocationExpressionSyntax invocation in source.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetOperation(invocation) is not IInvocationOperation operation
                || !operation.TargetMethod.Parameters.Any(parameter => parameter.Type.ToDisplayString() == "System.Delegate"))
            {
                continue;
            }

            typedCallSites++;
            model.GetInterceptorMethod(invocation).ShouldNotBeNull($"'{invocation}' is not intercepted");
        }

        typedCallSites.ShouldBeGreaterThan(0);
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

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a body read leaves serialization faults to the exception boundary")]
    public void Generator_BodyParameter_LetsSerializationFaultsPropagate()
    {
        // Act
        GeneratorRun run = Generate("""app.MapPost("/widgets", (Widget widget) => "ok");""");

        // Assert — the 415 is decided by the non-throwing lookup before the read; a missing registry or
        // contract surfaced by the read is not caught, and the probe compiles.
        run.Generated.ShouldContain("if (__serializer0 is not null", Case.Sensitive);
        run.Generated.ShouldContain("!global::Assimalign.Cohesion.Http.HttpMediaType.TryParse(", Case.Sensitive);
        run.Generated.ShouldContain("|| __serializer0.GetReader(__mediaType0) is null))", Case.Sensitive);
        run.Generated.ShouldContain("catch (global::System.Text.Json.JsonException)", Case.Sensitive);
        run.Generated.ShouldNotContain("HttpContentSerializationException", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: explicit header attribute overrides inference")]
    public void Generator_HeaderAttribute_EmitsHeaderBinding()
    {
        string generated = Run("""app.MapGet("/whoami", async ([FromHeader(Name = "X-User")] string user, IHttpContext context) => { await Task.CompletedTask; });""");

        generated.ShouldContain("context.Request.Headers.GetValue(\"X-User\")", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: attribute-supplied names with quotes, backslashes and control characters are escaped")]
    public void Generator_AttributeNamesWithSpecialCharacters_EmitEscapedLiterals()
    {
        // Act — each Name holds a character that ends or alters a C# string literal when spliced verbatim.
        GeneratorRun run = Generate(
            """
            app.MapGet("/escape", ([FromQuery(Name = "a\"b")] string quoted, [FromQuery(Name = "c\\d")] int slashed, [FromHeader(Name = "X-\tTab")] string tabbed) => "ok");
            app.MapPost("/form", ([FromForm(Name = "f\"\\\n")] string field) => "ok");
            app.MapGroup("api/{tenant}").MapGet("items", ([FromRoute(Name = "t\"enant")] string tenant) => "ok");
            """);

        // Assert — every read, every error key and every description name is the escaped literal.
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("context.Request.Query.TryGetValue(\"a\\\"b\", out var __query0)", Case.Sensitive);
        run.Generated.ShouldContain("context.Request.Query.TryGetValue(\"c\\\\d\", out var __query1)", Case.Sensitive);
        run.Generated.ShouldContain("context.Request.Headers.GetValue(\"X-\\tTab\")", Case.Sensitive);
        run.Generated.ShouldContain("__form.TryGetValue(\"f\\\"\\\\\\n\", out var __field0)", Case.Sensitive);
        run.Generated.ShouldContain("__routeValues0.TryGetValue(\"t\\\"enant\", out __raw0)", Case.Sensitive);
        run.Generated.ShouldContain("[\"a\\\"b\"] = new string[] { \"The value is required.\" }", Case.Sensitive);
        run.Generated.ShouldContain("[\"c\\\\d\"] = new string[] { \"The value could not be parsed.\" }", Case.Sensitive);
        run.Generated.ShouldContain("new global::Assimalign.Cohesion.Web.EndpointParameterMetadata(\"X-\\tTab\"", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
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

        // Assert — the requirement is chained onto the route the raw Map overload returns, after the
        // endpoint's description, so the caller's own chain still follows it.
        int description = generated.IndexOf("new global::Assimalign.Cohesion.Web.EndpointParameterMetadata(\"title\", global::Assimalign.Cohesion.Web.EndpointParameterSource.Form", StringComparison.Ordinal);
        int requirement = generated.IndexOf("            " + antiforgeryRequirement, StringComparison.Ordinal);

        generated.ShouldContain("await context.ReadFormAsync(context.RequestCancelled);", Case.Sensitive);
        description.ShouldBeGreaterThan(0);
        requirement.ShouldBeGreaterThan(description);
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
        GeneratorRun run = Generate(
            """app.MapPost("/orders", async ([FromForm] string title, IHttpContext context) => { await Task.CompletedTask; }).WithMetadata(Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata.Disabled);""",
            referenceAntiforgery: true);

        // Assert — the interceptor (requirement included) is part of a compilation with no errors.
        run.Generated.ShouldContain(antiforgeryRequirement, Case.Sensitive);
        run.Output.SyntaxTrees.Count().ShouldBe(2);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
    }

    // ---------------------------------------------------------------------
    // Returned values (#1059)
    // ---------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a returned value is written through content negotiation")]
    public void Generator_ValueReturn_WritesNegotiatedContent()
    {
        // Act
        string generated = Run("""app.MapGet("/widgets/{id}", (int id) => new Widget());""");

        // Assert — invoked as its natural Func type, null answers 204, anything else is negotiated.
        generated.ShouldContain("(global::System.Func<global::System.Int32, global::Widget>)handler", Case.Sensitive);
        generated.ShouldContain("global::Widget __result = __handler(__arg0);", Case.Sensitive);
        generated.ShouldContain("if (__result is null)", Case.Sensitive);
        generated.ShouldContain("context.Response.StatusCode.Equals(global::Assimalign.Cohesion.Http.HttpStatusCode.Ok)", Case.Sensitive);
        generated.ShouldContain("context.Response.StatusCode = global::Assimalign.Cohesion.Http.HttpStatusCode.NoContent;", Case.Sensitive);
        generated.ShouldContain("await context.WriteNegotiatedContentAsync<global::Widget>(__result, context.RequestCancelled);", Case.Sensitive);
        generated.ShouldContain("using Assimalign.Cohesion.Web.Serialization;", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a Task<T> handler's value is awaited, then written")]
    public void Generator_TaskOfValueReturn_AwaitsThenWrites()
    {
        // Act
        string generated = Run("""app.MapGet("/widgets/{id}", async (int id) => { await Task.Yield(); return new Widget(); });""");

        // Assert
        generated.ShouldContain("(global::System.Func<global::System.Int32, global::System.Threading.Tasks.Task<global::Widget>>)handler", Case.Sensitive);
        generated.ShouldContain("global::Widget __result = await __handler(__arg0);", Case.Sensitive);
        generated.ShouldContain("await context.WriteNegotiatedContentAsync<global::Widget>(__result, context.RequestCancelled);", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a ValueTask<T> handler's value is awaited, then written")]
    public void Generator_ValueTaskOfValueReturn_AwaitsThenWrites()
    {
        // Act
        string generated = Run("""app.MapGet("/widgets/{id}", (int id) => ValueTask.FromResult(new Widget()));""");

        // Assert
        generated.ShouldContain("(global::System.Func<global::System.Int32, global::System.Threading.Tasks.ValueTask<global::Widget>>)handler", Case.Sensitive);
        generated.ShouldContain("global::Widget __result = await __handler(__arg0);", Case.Sensitive);
        generated.ShouldContain("await context.WriteNegotiatedContentAsync<global::Widget>(__result, context.RequestCancelled);", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a returned string is written as text/plain without the serializer")]
    public void Generator_StringReturn_WritesTextPlain()
    {
        // Act — the application does not reference Web.Serialization, which text does not need.
        GeneratorRun run = Generate("""app.MapGet("/ping", () => "pong");""", referenceSerialization: false);

        // Assert
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("global::System.String __result = __handler();", Case.Sensitive);
        run.Generated.ShouldContain("if (__result is null)", Case.Sensitive);
        run.Generated.ShouldContain("context.Response.Headers[global::Assimalign.Cohesion.Http.HttpHeaderKey.ContentType] = \"text/plain; charset=utf-8\";", Case.Sensitive);
        run.Generated.ShouldContain("await context.Response.Body.WriteAsync(global::System.Text.Encoding.UTF8.GetBytes(__result), context.RequestCancelled);", Case.Sensitive);
        run.Generated.ShouldNotContain("WriteNegotiatedContentAsync", Case.Sensitive);
        run.Generated.ShouldNotContain("using Assimalign.Cohesion.Web.Serialization;", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a value-type result is written without a null check")]
    public void Generator_ValueTypeReturn_WritesWithoutNullCheck()
    {
        // Act
        string generated = Run("""app.MapGet("/count", () => 42);""");

        // Assert
        generated.ShouldContain("global::System.Int32 __result = __handler();", Case.Sensitive);
        generated.ShouldNotContain("__result is null", Case.Sensitive);
        generated.ShouldContain("await context.WriteNegotiatedContentAsync<global::System.Int32>(__result, context.RequestCancelled);", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a Nullable<T> result is null-checked and written as its underlying value")]
    public void Generator_NullableValueTypeReturn_WritesUnderlyingValue()
    {
        // Act
        string generated = Run("""app.MapGet("/limit", (int? limit) => limit);""");

        // Assert
        generated.ShouldContain("global::System.Int32? __result = __handler(__arg0);", Case.Sensitive);
        generated.ShouldContain("if (__result is null)", Case.Sensitive);
        generated.ShouldContain("await context.WriteNegotiatedContentAsync<global::System.Int32>(__result.Value, context.RequestCancelled);", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a nullable reference result is written as its non-nullable type")]
    public void Generator_NullableReferenceReturn_WritesNonNullableTypeArgument()
    {
        // Act — an explicit return type keeps the annotation on the handler's delegate type.
        string generated = Run("""app.MapGet("/widgets/{id}", Widget? (int id) => id > 0 ? new Widget() : null);""");

        // Assert
        generated.ShouldContain("(global::System.Func<global::System.Int32, global::Widget?>)handler", Case.Sensitive);
        generated.ShouldContain("global::Widget? __result = __handler(__arg0);", Case.Sensitive);
        generated.ShouldContain("if (__result is null)", Case.Sensitive);
        generated.ShouldContain("await context.WriteNegotiatedContentAsync<global::Widget>(__result, context.RequestCancelled);", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: every supported return shape compiles with no errors")]
    public void Generator_SupportedReturnShapes_GeneratedCodeCompiles()
    {
        // Act
        GeneratorRun run = Generate(
            """
            app.MapGet("/a/{id}", (int id) => new Widget());
            app.MapGet("/b/{id}", async (int id) => { await Task.Yield(); return new Widget(); });
            app.MapGet("/c/{id}", (int id) => ValueTask.FromResult(new Widget()));
            app.MapGet("/d", () => "text");
            app.MapGet("/e", async () => { await Task.Yield(); return (string?)null; });
            app.MapGet("/f", () => 42);
            app.MapGet("/g", (int? limit) => limit);
            app.MapGet("/h/{id}", (int id) => id > 0 ? new Widget() : null);
            app.MapPost("/i", (Widget widget) => widget);
            app.MapGet("/j", () => { });
            app.MapGet("/k", () => Task.CompletedTask);
            app.MapGet("/l/{id}", GetWidget);
            """,
            members: "    private static Task<Widget?> GetWidget(int id) => Task.FromResult<Widget?>(null);");

        // Assert
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("Intercept_11(", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
        AssertEveryTypedCallSiteIsIntercepted(run);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: a method-group handler is intercepted like a lambda")]
    public void Generator_MethodGroupHandler_IsIntercepted()
    {
        // Act
        GeneratorRun run = Generate(
            """app.MapGet("/widgets/{id}", GetWidget);""",
            members: "    private static Widget GetWidget(int id) => new Widget();");

        // Assert
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("(global::System.Func<global::System.Int32, global::Widget>)handler", Case.Sensitive);
        run.Generated.ShouldContain("__routeValues0.TryGetValue(\"id\"", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Generator: named arguments in any order are intercepted")]
    public void Generator_NamedArgumentsOutOfOrder_AreIntercepted()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet(handler: (int id) => id, pattern: "/items/{id}");""");

        // Assert
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("__routeValues0.TryGetValue(\"id\"", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
    }

    // ---------------------------------------------------------------------
    // Receivers (#1174)
    // ---------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Receivers: a type-parameter application receiver gets a generic interceptor")]
    public void Generator_TypeParameterApplicationReceiver_EmitsGenericInterceptor()
    {
        // Act — TApp is not in scope in the generated file, so the interceptor cannot spell it out.
        GeneratorRun run = Generate(
            "",
            members: """
                public static void Configure<TApp>(TApp app) where TApp : IWebApplicationPipelineBuilder, IWebApplication
                {
                    app.MapGet("/items/{id}", (int id) => "item");
                }
            """);

        // Assert — generic over the implementation's own type parameter, with its constraints, and the
        // output compiles.
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("Intercept_0<TBuilder>(this TBuilder builder, string pattern, global::System.Delegate handler)", Case.Sensitive);
        run.Generated.ShouldContain("            where TBuilder : ", Case.Sensitive);
        run.Generated.ShouldContain("global::Assimalign.Cohesion.Web.IWebApplicationPipelineBuilder, global::Assimalign.Cohesion.Web.IWebApplication", Case.Sensitive);
        run.Generated.ShouldNotContain("TApp", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
        AssertEveryTypedCallSiteIsIntercepted(run);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Receivers: a type-parameter group receiver is intercepted over IRouterGroupBuilder")]
    public void Generator_TypeParameterGroupReceiver_InterceptsOverGroupBuilder()
    {
        // Act — the call binds the group extension, whose receiver is IRouterGroupBuilder, not TGroup.
        GeneratorRun run = Generate(
            "",
            members: """
                public static void Configure<TGroup>(TGroup group) where TGroup : Assimalign.Cohesion.Web.Routing.IRouterGroupBuilder
                {
                    group.MapPost("items", (Widget widget) => widget);
                }
            """);

        // Assert
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("Intercept_0(this global::Assimalign.Cohesion.Web.Routing.IRouterGroupBuilder builder", Case.Sensitive);
        run.Generated.ShouldNotContain("TGroup", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
        AssertEveryTypedCallSiteIsIntercepted(run);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Receivers: conditional-access calls are intercepted like ordinary calls")]
    public void Generator_ConditionalAccessCalls_AreIntercepted()
    {
        // Act — app?.MapGet is a member binding, not a member access.
        GeneratorRun run = Generate(
            """
            WebApplication? maybe = app;
            maybe?.MapGet("/items/{id}", (int id) => "item");
            maybe?.MapGroup("api")?.MapPost("items", (Widget widget) => widget);
            """);

        // Assert — both are intercepted over the receiver the extension declares, and compile.
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("Intercept_0(this global::Assimalign.Cohesion.Web.Hosting.WebApplication builder", Case.Sensitive);
        run.Generated.ShouldContain("Intercept_1(this global::Assimalign.Cohesion.Web.Routing.IRouterGroupBuilder builder", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
        AssertEveryTypedCallSiteIsIntercepted(run);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Receivers: static-form calls are intercepted")]
    public void Generator_StaticFormCalls_AreIntercepted()
    {
        // Act — the static form passes the receiver as the first argument.
        GeneratorRun run = Generate(
            """
            WebApplicationPipelineBuilderExtensions.MapGet(app, "/items/{id}", (int id) => "item");
            WebApplicationPipelineBuilderExtensions.MapGet<WebApplication>(app, "/typed/{id}", (int id) => id);
            WebApplicationPipelineBuilderExtensions.Map(app, HttpMethod.Put, "/put/{id}", (int id, Widget widget) => widget);
            RouterGroupBuilderEndpointExtensions.MapPost(app.MapGroup("api"), "items", (Widget widget) => widget);
            """,
            members: """
                public static void Module<TApp>(TApp app) where TApp : IWebApplicationPipelineBuilder, IWebApplication
                {
                    WebApplicationPipelineBuilderExtensions.MapDelete(app, "/module/{id}", (int id) => "deleted");
                }
            """);

        // Assert — the receiver comes from the implementation's first parameter, generic for TApp.
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.ShouldContain("(this global::Assimalign.Cohesion.Web.Hosting.WebApplication builder, string pattern", Case.Sensitive);
        run.Generated.ShouldContain("(this global::Assimalign.Cohesion.Web.Hosting.WebApplication builder, global::Assimalign.Cohesion.Http.HttpMethod method, string pattern", Case.Sensitive);
        run.Generated.ShouldContain("(this global::Assimalign.Cohesion.Web.Routing.IRouterGroupBuilder builder, string pattern", Case.Sensitive);
        run.Generated.ShouldContain("<TBuilder>(this TBuilder builder, string pattern", Case.Sensitive);
        run.Generated.ShouldNotContain("WebApplicationPipelineBuilderExtensions builder", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
        AssertEveryTypedCallSiteIsIntercepted(run);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Receivers: a delegate instance wrapped in a delegate creation reports COHWEB0001")]
    public void Generator_WrappedDelegateInstance_ReportsCohweb0001()
    {
        // Act — new Func<...>(existing) is a delegate creation, but over an instance, not a lambda or method.
        GeneratorRun run = Generate(
            """
            System.Func<int, string> existing = id => "item";
            app.MapGet("/items/{id}", new System.Func<int, string>(existing));
            """);

        // Assert — reported rather than left to the throwing placeholder.
        Diagnostic diagnostic = run.WithId("COHWEB0001").ShouldHaveSingleItem();
        run.TextAt(diagnostic).ShouldBe("new System.Func<int, string>(existing)");
        run.Generated.ShouldNotContain("Intercept_", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Receivers: a concrete receiver keeps a non-generic interceptor")]
    public void Generator_ConcreteApplicationReceiver_EmitsNonGenericInterceptor()
    {
        // Act
        string generated = Run("""app.MapGet("/items/{id}", (int id) => "item");""");

        // Assert
        generated.ShouldContain("Intercept_0(this global::Assimalign.Cohesion.Web.Hosting.WebApplication builder, string pattern, global::System.Delegate handler)", Case.Sensitive);
        generated.ShouldNotContain("where ", Case.Sensitive);
    }

    // ---------------------------------------------------------------------
    // Endpoint description metadata (#152)
    // ---------------------------------------------------------------------

    private const string parameterMetadata = "new global::Assimalign.Cohesion.Web.EndpointParameterMetadata(";
    private const string responseMetadata = "new global::Assimalign.Cohesion.Web.EndpointResponseMetadata(";
    private const string noContentResponse = responseMetadata + "global::Assimalign.Cohesion.Http.HttpStatusCode.NoContent, null, null)";

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Description: request-bound parameters and the response are described with typeof values")]
    public void Generator_TypedEndpoint_DescribesParametersAndResponse()
    {
        // Act
        string generated = Run("""app.MapGet("/widgets/{id}", (int id, [FromQuery(Name = "q")] string? filter, [FromHeader(Name = "X-Tenant")] string tenant, IHttpContext context) => new Widget());""");

        // Assert — the injected context is not a request input, so three parameters are described.
        generated.ShouldContain(parameterMetadata + "\"id\", global::Assimalign.Cohesion.Web.EndpointParameterSource.Route, typeof(global::System.Int32), true)", Case.Sensitive);
        generated.ShouldContain(parameterMetadata + "\"q\", global::Assimalign.Cohesion.Web.EndpointParameterSource.Query, typeof(global::System.String), false)", Case.Sensitive);
        generated.ShouldContain(parameterMetadata + "\"X-Tenant\", global::Assimalign.Cohesion.Web.EndpointParameterSource.Header, typeof(global::System.String), true)", Case.Sensitive);
        generated.Split(parameterMetadata).Length.ShouldBe(4);
        generated.ShouldContain(responseMetadata + "global::Assimalign.Cohesion.Http.HttpStatusCode.Ok, typeof(global::Widget), null)", Case.Sensitive);
        generated.ShouldNotContain(noContentResponse, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Description: a body and a group parameter describe their sources")]
    public void Generator_BodyAndGroupParameters_DescribeSources()
    {
        // Act
        string generated = Run("""app.MapGroup("api/{tenant}").MapPost("orders", (string tenant, Widget widget) => "ok");""");

        // Assert — the group prefix is not visible, so tenant is route-or-query; a body is always required.
        generated.ShouldContain(parameterMetadata + "\"tenant\", global::Assimalign.Cohesion.Web.EndpointParameterSource.RouteOrQuery, typeof(global::System.String), true)", Case.Sensitive);
        generated.ShouldContain(parameterMetadata + "\"widget\", global::Assimalign.Cohesion.Web.EndpointParameterSource.Body, typeof(global::Widget), true)", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Description: a string result is described as text/plain")]
    public void Generator_StringResult_DescribesTextPlain()
    {
        // Act
        string generated = Run("""app.MapGet("/ping", () => "pong");""");

        // Assert
        generated.ShouldContain(responseMetadata + "global::Assimalign.Cohesion.Http.HttpStatusCode.Ok, typeof(global::System.String), global::Assimalign.Cohesion.Http.HttpMediaType.TextPlain)", Case.Sensitive);
        generated.ShouldNotContain(noContentResponse, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Description: a handler that writes its own response is described without a type")]
    public void Generator_VoidHandler_DescribesResponseWithoutType()
    {
        // Act
        string generated = Run("""app.MapGet("/users/{id}", async (int id, IHttpContext context) => { await Task.CompletedTask; });""");

        // Assert
        generated.ShouldContain(responseMetadata + "global::Assimalign.Cohesion.Http.HttpStatusCode.Ok, null, null)", Case.Sensitive);
        generated.ShouldNotContain(noContentResponse, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Description: a result that may be null also describes 204")]
    public void Generator_NullableResults_DescribeNoContent()
    {
        // Act — an annotated lambda return, a method group returning Task<Widget?>, a Nullable<T>, and two
        // inferred lambda returns whose null-state is maybe-null.
        GeneratorRun run = Generate(
            """
            app.MapGet("/a/{id}", Widget? (int id) => null);
            app.MapGet("/b/{id}", FindWidget);
            app.MapGet("/c", (int? limit) => limit);
            app.MapGet("/d/{id}", (int id) => id > 0 ? new Widget() : null);
            app.MapGet("/e/{id}", async (int id) => await FindWidget(id));
            """,
            members: "    private static Task<Widget?> FindWidget(int id) => Task.FromResult<Widget?>(null);");

        // Assert — each 200 describes the written type (Int32 for int?), and each endpoint lists the 204.
        run.Diagnostics.ShouldBeEmpty(Describe(run.Diagnostics));
        run.Generated.Split(noContentResponse).Length.ShouldBe(6);
        run.Generated.ShouldContain(responseMetadata + "global::Assimalign.Cohesion.Http.HttpStatusCode.Ok, typeof(global::Widget), null)", Case.Sensitive);
        run.Generated.ShouldContain(responseMetadata + "global::Assimalign.Cohesion.Http.HttpStatusCode.Ok, typeof(global::System.Int32), null)", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Description: a nested lambda's null return does not describe 204 for the handler")]
    public void Generator_NestedLambdaNullReturn_DescribesNoNoContent()
    {
        // Act — only the outer lambda's returns decide; the nested one's null is its own.
        string generated = Run(
            """
            app.MapGet("/widgets/{id}", (int id) =>
            {
                System.Func<Widget?> fallback = () => null;
                return fallback() ?? new Widget();
            });
            """);

        // Assert
        generated.ShouldContain(responseMetadata + "global::Assimalign.Cohesion.Http.HttpStatusCode.Ok, typeof(global::Widget), null)", Case.Sensitive);
        generated.ShouldNotContain(noContentResponse, Case.Sensitive);
    }

    // ---------------------------------------------------------------------
    // Diagnostics (#1059)
    // ---------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a delegate instance handler reports COHWEB0001")]
    public void Generator_DelegateInstanceHandler_ReportsCohweb0001()
    {
        // Act
        GeneratorRun run = Generate(
            """
            System.Func<int, Task> handler = id => Task.CompletedTask;
            app.MapGet("/items/{id}", handler);
            """);

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0001").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        run.TextAt(diagnostic).ShouldBe("handler");
        diagnostic.GetMessage().ShouldContain("MapGet(\"/items/{id}\")", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("pass a lambda expression or a method group", Case.Sensitive);
        run.Generated.ShouldNotContain("Intercept_", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a returned stream reports COHWEB0002")]
    public void Generator_StreamReturn_ReportsCohweb0002()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/download", () => new System.IO.MemoryStream());""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0002").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        run.TextAt(diagnostic).ShouldBe("() =>");
        diagnostic.GetMessage().ShouldContain("returns 'MemoryStream'", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("copy it to context.Response.Body", Case.Sensitive);
        run.Generated.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: an async void method group reports COHWEB0002")]
    public void Generator_AsyncVoidMethodGroup_ReportsCohweb0002()
    {
        // Act
        GeneratorRun run = Generate(
            """app.MapPost("/fire/{id}", Fire);""",
            members: "    private static async void Fire(int id) { await Task.Yield(); }");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0002").ShouldHaveSingleItem();
        run.TextAt(diagnostic).ShouldBe("Fire");
        diagnostic.GetMessage().ShouldContain("async void", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("declare it async Task", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: an anonymous-type result reports COHWEB0002")]
    public void Generator_AnonymousTypeReturn_ReportsCohweb0002()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/anonymous", () => new { Id = 1 });""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0002").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("anonymous types cannot be named", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("use a named record or class", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: an awaited value that is itself awaitable reports COHWEB0002")]
    public void Generator_NestedAwaitableReturn_ReportsCohweb0002()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/nested", async () => { await Task.Yield(); return Task.FromResult(1); });""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0002").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("the awaited value 'Task<int>' is itself awaitable", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a complex type read from the query string reports COHWEB0003 at the parameter")]
    public void Generator_ComplexQueryParameter_ReportsCohweb0003()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/search", ([FromQuery] Widget filter) => "ok");""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0003").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        run.TextAt(diagnostic).ShouldBe("filter");
        diagnostic.GetMessage().ShouldContain("Parameter 'filter'", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("cannot be read from a single query string value", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("[FromBody]", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a parameter default value reports COHWEB0003")]
    public void Generator_DefaultParameterValue_ReportsCohweb0003()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/paged", (int page = 1) => page);""");

        // Assert — the default is the cause, so no generic delegate-type diagnostic is added.
        Diagnostic diagnostic = run.WithId("COHWEB0003").ShouldHaveSingleItem();
        run.TextAt(diagnostic).ShouldBe("page");
        diagnostic.GetMessage().ShouldContain("declares a default value", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("int? page", Case.Sensitive);
        run.WithId("COHWEB0006").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a ref struct parameter reports COHWEB0003")]
    public void Generator_RefStructParameter_ReportsCohweb0003()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/span", (System.ReadOnlySpan<char> q) => "ok");""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0003").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("is a ref struct", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a by-reference parameter reports COHWEB0003")]
    public void Generator_ByReferenceParameter_ReportsCohweb0003()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/ref", (ref int id) => "ok");""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0003").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("passed by reference", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a private parameter type reports COHWEB0003")]
    public void Generator_PrivateParameterType_ReportsCohweb0003()
    {
        // Act
        GeneratorRun run = Generate(
            """app.MapPost("/secrets", (Secret secret) => "ok");""",
            members: "    private sealed class Secret { public string Value { get; set; } = \"\"; }");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0003").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("'Endpoints.Secret' is not accessible to generated code", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("make it internal or public", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: two request-body parameters report COHWEB0004")]
    public void Generator_TwoBodyParameters_ReportsCohweb0004()
    {
        // Act
        GeneratorRun run = Generate("""app.MapPost("/pair", (Widget first, Widget second) => "ok");""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0004").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        run.TextAt(diagnostic).ShouldBe("second");
        diagnostic.GetMessage().ShouldContain("binds both 'first' and 'second' from the request body", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a request body with form fields reports COHWEB0005")]
    public void Generator_BodyAndFormParameters_ReportsCohweb0005()
    {
        // Act
        GeneratorRun run = Generate("""app.MapPost("/mixed", (Widget widget, [FromForm] string title) => "ok");""");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0005").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        run.TextAt(diagnostic).ShouldBe("title");
        diagnostic.GetMessage().ShouldContain("binds 'widget' from the request body and 'title' from form fields", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: seventeen parameters report COHWEB0006")]
    public void Generator_SeventeenParameters_ReportsCohweb0006()
    {
        // Arrange
        string parameters = string.Join(", ", Enumerable.Range(1, 17).Select(index => "int p" + index));

        // Act
        GeneratorRun run = Generate("app.MapGet(\"/wide\", (" + parameters + ") => \"ok\");");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0006").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("declares 17 parameters", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("group request values into a [FromBody] model", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a private delegate type reports COHWEB0006")]
    public void Generator_PrivateDelegateType_ReportsCohweb0006()
    {
        // Act
        GeneratorRun run = Generate(
            """app.MapGet("/items/{id}", new ItemHandler((int id) => "ok"));""",
            members: "    private delegate string ItemHandler(int id);");

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0006").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("its delegate type 'Endpoints.ItemHandler' cannot be named", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a serialized result without Web.Serialization reports COHWEB0007")]
    public void Generator_ValueReturnWithoutSerialization_ReportsCohweb0007()
    {
        // Act
        GeneratorRun run = Generate("""app.MapGet("/widgets/{id}", (int id) => new Widget());""", referenceSerialization: false);

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0007").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("returns 'Widget', which is written through content negotiation", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("Assimalign.Cohesion.Web.Serialization", Case.Sensitive);
        run.Generated.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: a request body without Web.Serialization reports COHWEB0007")]
    public void Generator_BodyWithoutSerialization_ReportsCohweb0007()
    {
        // Act
        GeneratorRun run = Generate(
            """app.MapPost("/widgets", (Widget widget, IHttpContext context) => Task.CompletedTask);""",
            referenceSerialization: false);

        // Assert
        Diagnostic diagnostic = run.WithId("COHWEB0007").ShouldHaveSingleItem();
        run.TextAt(diagnostic).ShouldBe("widget");
        diagnostic.GetMessage().ShouldContain("binds 'widget' from the request body", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SourceGeneration] - Diagnostics: an unsupported call site leaves the others intercepted")]
    public void Generator_UnsupportedCallSite_LeavesOtherEndpointsIntercepted()
    {
        // Act
        GeneratorRun run = Generate(
            """
            app.MapGet("/good/{id}", (int id) => new Widget());
            app.MapGet("/bad", () => new System.IO.MemoryStream());
            """);

        // Assert — one diagnostic, one interceptor, and the interceptor still compiles.
        run.Diagnostics.ShouldHaveSingleItem().Id.ShouldBe("COHWEB0002");
        run.Generated.ShouldContain("Intercept_0(", Case.Sensitive);
        run.Generated.ShouldNotContain("Intercept_1(", Case.Sensitive);
        run.CompileErrors.ShouldBeEmpty(Describe(run.CompileErrors));
    }
}
