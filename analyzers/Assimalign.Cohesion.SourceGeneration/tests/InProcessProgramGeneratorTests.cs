using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.SourceGeneration.Tests;

/// <summary>
/// Generator-driver coverage for in-process program bindings: the bridge a resource program's build
/// emits, and the catalog registrations a gateway program's build emits for the resources it
/// references. Each test compiles the generated code against stand-ins for the hosting and gateway
/// packages, and the run-time tests load and call what was compiled.
/// </summary>
public class InProcessProgramGeneratorTests
{
    private const string DisplayPrefix = "Cohesion Test [SourceGeneration.ApplicationModel] - In-process programs: ";

    private const string HostingAssembly = "Assimalign.Cohesion.ApplicationModel.Hosting.InProcess";
    private const string GatewayAssembly = "Assimalign.Cohesion.ApplicationModel.Gateway";
    private const string GatewayInProcessAssembly = "Assimalign.Cohesion.ApplicationModel.Gateway.InProcess";
    private const string WebIntegrationAssembly = "Assimalign.Cohesion.Web.Hosting.ApplicationModel";
    private const string DatabaseIntegrationAssembly = "Assimalign.Cohesion.Database.Hosting.ApplicationModel";
    private const string ProbeAssembly = "Test.Probe";

    private static readonly IReadOnlyList<MetadataReference> _platformReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Where(path => !Path.GetFileName(path).StartsWith("Assimalign.Cohesion.", StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();

    private static readonly CSharpParseOptions _parseOptions = new(LanguageVersion.Preview);

    // Stand-ins for the packages, with the names and shapes the generators look for.
    private static readonly byte[] _hosting = CompileImage(HostingAssembly, """
        using System;

        namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;

        [AttributeUsage(AttributeTargets.Assembly)]
        public sealed class InProcessProgramAttribute : Attribute
        {
            public InProcessProgramAttribute(Type program) { Program = program; }

            public InProcessProgramAttribute(Type program, string? provider) : this(program) { Provider = provider; }

            public Type Program { get; }

            public string? Provider { get; }
        }
        """);

    private static readonly byte[] _gateway = CompileImage(GatewayAssembly, """
        namespace Assimalign.Cohesion.ApplicationModel.Gateway;

        public sealed class ApplicationGateway
        {
        }
        """);

    private static readonly byte[] _gatewayInProcess = CompileImage(GatewayInProcessAssembly, """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

        public static class InProcessProgramCatalog
        {
            public static List<(string Provider, string Name, Func<string[], Task> Main)> Programs { get; } = new();

            public static void Register(string provider, string name, Func<string[], Task> main) => Programs.Add((provider, name, main));
        }
        """);

    private static readonly byte[] _webIntegration = CompileImage(WebIntegrationAssembly, """
        namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel;

        public static class WebResourceMarker
        {
        }
        """);

    private static readonly byte[] _databaseIntegration = CompileImage(DatabaseIntegrationAssembly, """
        namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel;

        public static class DatabaseResourceMarker
        {
        }
        """);

    // What a program records when it runs, so a test can tell its entry point was called.
    private static readonly byte[] _probe = CompileImage(ProbeAssembly, """
        using System.Collections.Generic;

        namespace Test.Probe;

        public static class Probe
        {
            public static List<string> Calls { get; } = new();
        }
        """);

    [Fact(DisplayName = DisplayPrefix + "An asynchronous top-level program's bridge calls its entry point at run time")]
    public async Task Bridge_TopLevelTask_ShouldCallTheEntryPointAsync()
    {
        // Arrange
        GeneratorRun run = RunResource("Shop.Orders", """
            using Test.Probe;

            await System.Threading.Tasks.Task.Yield();
            Probe.Calls.Add("task:" + string.Join(",", args));
            """);

        // Act
        string[] calls = await InvokeBridgeAsync(run, ["a", "b"]);

        // Assert
        string bridge = run.Generated("InProcessEntryPoint.g.cs");
        bridge.ShouldContain("namespace Assimalign.Cohesion.Generated.Shop.Orders;", Case.Sensitive);
        bridge.ShouldContain("UnsafeAccessorKind.StaticMethod, Name = \"<Main>$\"", Case.Sensitive);
        bridge.ShouldContain("CallEntryPoint(global::Program? program, string[] args)", Case.Sensitive);
        bridge.ShouldContain("[assembly: global::Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.InProcessProgram(typeof(global::Assimalign.Cohesion.Generated.Shop.Orders.InProcessEntryPoint), \"Web\")]", Case.Sensitive);
        calls.ShouldBe(["task:a,b"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A top-level program that returns Task<int> completes on zero and faults on another exit code")]
    public async Task Bridge_TopLevelTaskOfInt_ShouldFaultOnNonzeroExitCodeAsync()
    {
        // Arrange
        GeneratorRun run = RunResource("Shop.Orders", """
            using Test.Probe;

            await System.Threading.Tasks.Task.Yield();
            Probe.Calls.Add("exit:" + args[0]);
            return int.Parse(args[0]);
            """);

        // Act
        string[] succeeded = await InvokeBridgeAsync(run, ["0"]);
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => InvokeBridgeAsync(run, ["3"]));

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("global::System.Threading.Tasks.Task<int> CallEntryPoint(", Case.Sensitive);
        succeeded.ShouldBe(["exit:0"]);
        failure.Message.ShouldContain("exited with code 3", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A synchronous top-level program's bridge runs it to completion")]
    public async Task Bridge_TopLevelVoid_ShouldRunItToCompletionAsync()
    {
        // Arrange
        GeneratorRun run = RunResource("Shop.Orders", """
            Test.Probe.Probe.Calls.Add("void:" + args.Length);
            """);

        // Act
        string[] calls = await InvokeBridgeAsync(run, ["a"]);

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("private static extern void CallEntryPoint(global::Program? program, string[] args);", Case.Sensitive);
        calls.ShouldBe(["void:1"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A synchronous top-level program that returns an exit code faults on a nonzero one")]
    public async Task Bridge_TopLevelInt_ShouldFaultOnNonzeroExitCodeAsync()
    {
        // Arrange
        GeneratorRun run = RunResource("Shop.Orders", """
            Test.Probe.Probe.Calls.Add("int");
            return args.Length;
            """);

        // Act
        string[] succeeded = await InvokeBridgeAsync(run, []);
        await Should.ThrowAsync<InvalidOperationException>(() => InvokeBridgeAsync(run, ["x"]));

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("private static extern int CallEntryPoint(global::Program? program, string[] args);", Case.Sensitive);
        succeeded.ShouldBe(["int"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A top-level program that does not read its arguments still receives them")]
    public async Task Bridge_TopLevelWithoutArguments_ShouldPassThemAsync()
    {
        // Arrange: a top-level program's entry point always declares args, whether or not it reads them.
        GeneratorRun run = RunResource("Shop.Orders", """
            Test.Probe.Probe.Calls.Add("ran");
            """);

        // Act
        string[] calls = await InvokeBridgeAsync(run, []);

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("CallEntryPoint(default, args);", Case.Sensitive);
        calls.ShouldBe(["ran"]);
    }

    [Fact(DisplayName = DisplayPrefix + "An accessible explicit Main is called directly")]
    public async Task Bridge_PublicMain_ShouldCallItDirectlyAsync()
    {
        // Arrange
        GeneratorRun run = RunResource("Shop.Orders", """
            using System.Threading.Tasks;

            namespace Shop.Orders;

            public static class Program
            {
                public static async Task Main()
                {
                    await Task.Yield();
                    Test.Probe.Probe.Calls.Add("public");
                }
            }
            """);

        // Act
        string[] calls = await InvokeBridgeAsync(run, ["ignored"]);

        // Assert
        string bridge = run.Generated("InProcessEntryPoint.g.cs");
        bridge.ShouldContain("return global::Shop.Orders.Program.Main();", Case.Sensitive);
        bridge.ShouldNotContain("UnsafeAccessor", Case.Sensitive);
        calls.ShouldBe(["public"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A private Main in a static class is reached through its type's runtime name")]
    public async Task Bridge_PrivateMainInStaticClass_ShouldReachItByTypeNameAsync()
    {
        // Arrange: a static class cannot be a parameter type, so the accessor names it instead.
        GeneratorRun run = RunResource("Shop.Orders", """
            namespace Shop.Orders;

            internal static class Program
            {
                private static int Main(string[] args)
                {
                    Test.Probe.Probe.Calls.Add("static:" + args.Length);
                    return 0;
                }
            }
            """);

        // Act
        string[] calls = await InvokeBridgeAsync(run, ["a", "b", "c"]);

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("[global::System.Runtime.CompilerServices.UnsafeAccessorType(\"Shop.Orders.Program, Shop.Orders\")] object? program", Case.Sensitive);
        calls.ShouldBe(["static:3"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A private Main in an instance class is reached through an accessor typed by the class")]
    public async Task Bridge_PrivateMainInClass_ShouldReachItByTypeAsync()
    {
        // Arrange
        GeneratorRun run = RunResource("Shop.Orders", """
            namespace Shop.Orders;

            internal sealed class Program
            {
                private static void Main(string[] args)
                {
                    Test.Probe.Probe.Calls.Add("class:" + args.Length);
                }
            }
            """);

        // Act
        string[] calls = await InvokeBridgeAsync(run, ["a"]);

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("CallEntryPoint(global::Shop.Orders.Program? program, string[] args);", Case.Sensitive);
        calls.ShouldBe(["class:1"]);
    }

    [Theory(DisplayName = DisplayPrefix + "An entry point in a file-local type is reached through the type's runtime name")]
    [InlineData("file class Program { private static void Main(string[] args) { Test.Probe.Probe.Calls.Add(\"file:\" + args.Length); } }")]
    [InlineData("file static class Program { public static void Main(string[] args) { Test.Probe.Probe.Calls.Add(\"file:\" + args.Length); } }")]
    [InlineData("file class Program { public static System.Threading.Tasks.Task<int> Main(string[] args) { Test.Probe.Probe.Calls.Add(\"file:\" + args.Length); return System.Threading.Tasks.Task.FromResult(0); } }")]
    public async Task Bridge_FileLocalProgram_ShouldReachItByTypeNameAsync(string program)
    {
        // Arrange: only its own file can name a file-local type, so the bridge can neither call it
        // nor take it as a parameter.
        GeneratorRun run = RunResource("Shop.Orders", "namespace Shop.Orders;\n\n" + program);

        // Act
        string[] calls = await InvokeBridgeAsync(run, ["a", "b"]);

        // Assert
        string bridge = run.Generated("InProcessEntryPoint.g.cs");
        bridge.ShouldContain("[global::System.Runtime.CompilerServices.UnsafeAccessorType(\"Shop.Orders.<", Case.Sensitive);
        bridge.ShouldNotContain("global::Shop.Orders.Program", Case.Sensitive);
        calls.ShouldBe(["file:2"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A type in a namespace of keywords is named without the escapes the runtime would not resolve")]
    public async Task Bridge_ProgramInKeywordNamespace_ShouldNameItWithoutEscapesAsync()
    {
        // Arrange
        GeneratorRun run = RunResource("Shop.Orders", """
            namespace @class.@event;

            internal static class Program
            {
                private static int Main(string[] args)
                {
                    Test.Probe.Probe.Calls.Add("keyword:" + args.Length);
                    return 0;
                }
            }
            """);

        // Act
        string[] calls = await InvokeBridgeAsync(run, ["a"]);

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("UnsafeAccessorType(\"class.event.Program, Shop.Orders\")", Case.Sensitive);
        calls.ShouldBe(["keyword:1"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A library gets no bridge")]
    public void Bridge_Library_ShouldEmitNothing()
    {
        // Act
        GeneratorRun run = Run("Shop.Library", "namespace Shop.Library; public static class Thing { }", OutputKind.DynamicallyLinkedLibrary, [_hosting, _webIntegration]);

        // Assert
        run.RunResult.GeneratedTrees.ShouldBeEmpty();
        run.AssertNoErrors();
    }

    [Fact(DisplayName = DisplayPrefix + "An executable that does not reference the in-process hosting package gets no bridge")]
    public void Bridge_WithoutHostingPackage_ShouldEmitNothing()
    {
        // Act
        GeneratorRun run = Run("Shop.Tool", "System.Console.WriteLine(args.Length);", OutputKind.ConsoleApplication, [_webIntegration]);

        // Assert
        run.RunResult.GeneratedTrees.ShouldBeEmpty();
        run.AssertNoErrors();
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway program is never a resource, so it gets no bridge")]
    public void Bridge_GatewayProgram_ShouldEmitNothing()
    {
        // Act
        GeneratorRun run = Run("Shop.Gateway", "System.Console.WriteLine(args.Length);", OutputKind.ConsoleApplication, [_hosting, _gateway, _gatewayInProcess]);

        // Assert
        run.RunResult.GeneratedTrees.ShouldBeEmpty();
        run.AssertNoErrors();
    }

    [Fact(DisplayName = DisplayPrefix + "A program that references several areas' integrations records no provider")]
    public void Bridge_WithSeveralAreaIntegrations_ShouldRecordNoProvider()
    {
        // Act
        GeneratorRun run = Run("Shop.Orders", "System.Console.WriteLine(args.Length);", OutputKind.ConsoleApplication, [_hosting, _webIntegration, _databaseIntegration]);

        // Assert
        run.Generated("InProcessEntryPoint.g.cs").ShouldContain("InProcessProgram(typeof(global::Assimalign.Cohesion.Generated.Shop.Orders.InProcessEntryPoint))]", Case.Sensitive);
        run.AssertNoErrors();
    }

    [Fact(DisplayName = DisplayPrefix + "A bridge's namespace is a valid name for any assembly name")]
    public void GetBridgeNamespace_WithUnusualAssemblyName_ShouldSanitizeEachSegment()
    {
        // Act & Assert
        InProcessProgramBridgeGenerator.GetBridgeNamespace("Shop-Orders.1st.class").ShouldBe("Assimalign.Cohesion.Generated.Shop_Orders._1st.@class");
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway program registers each referenced resource's program before its own code runs")]
    public async Task Catalog_WithReferencedResource_ShouldRegisterItsProgramAtModuleLoadAsync()
    {
        // Arrange
        byte[] orders = CompileResource("Shop.Orders", """
            Test.Probe.Probe.Calls.Add("orders:" + args.Length);
            """);

        // Act
        GeneratorRun gateway = RunGateway([("Shop.Orders", orders)], Resource("Shop.Orders", "orders-api"));

        // Assert: the registration compiles, and loading the gateway registers a program that runs the resource's entry point.
        gateway.Diagnostics.ShouldBeEmpty();
        gateway.RunResult.GeneratedTrees.Select(tree => Path.GetFileName(tree.FilePath)).ShouldBe(["InProcessProgramRegistrations.g.cs"]);
        gateway.Generated("InProcessProgramRegistrations.g.cs").ShouldContain(
            "global::Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.InProcessProgramCatalog.Register(\"Web\", \"orders-api\", global::Assimalign.Cohesion.Generated.Shop.Orders.InProcessEntryPoint.RunAsync);",
            Case.Sensitive);

        var context = new AssemblyLoadContext(nameof(Catalog_WithReferencedResource_ShouldRegisterItsProgramAtModuleLoadAsync), isCollectible: true);
        try
        {
            Func<string, Assembly> load = CreateLoader(context, new Dictionary<string, byte[]> { ["Shop.Orders"] = orders, [GatewayInProcessAssembly] = _gatewayInProcess });
            Assembly gatewayAssembly = context.LoadFromStream(new MemoryStream(gateway.Emit()));
            RuntimeHelpers.RunModuleConstructor(gatewayAssembly.ManifestModule.ModuleHandle);

            var programs = (System.Collections.IList)load(GatewayInProcessAssembly)
                .GetType("Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.InProcessProgramCatalog", throwOnError: true)!
                .GetProperty("Programs")!.GetValue(null)!;
            var program = ((string Provider, string Name, Func<string[], Task> Main))programs.Cast<object>().ShouldHaveSingleItem();
            program.Provider.ShouldBe("Web");
            program.Name.ShouldBe("orders-api");

            await program.Main([]);
            ReadProbe(load).ShouldBe(["orders:0"]);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Resources whose code declares the same namespace get bridges that do not collide")]
    public void Catalog_WithResourcesSharingARootNamespace_ShouldRegisterBoth()
    {
        // Arrange: both programs declare Shop.Program; their bridges are named for their assemblies.
        const string program = """
            namespace Shop;

            public static class Program
            {
                public static void Main(string[] args)
                {
                }
            }
            """;
        byte[] orders = CompileResource("Shop.Orders", program);
        byte[] billing = CompileResource("Shop.Billing", program);

        // Act
        GeneratorRun gateway = RunGateway(
            [("Shop.Orders", orders), ("Shop.Billing", billing)],
            Resource("Shop.Orders", "orders-api"),
            Resource("Shop.Billing", "billing-api"));

        // Assert
        gateway.Diagnostics.ShouldBeEmpty();
        string registrations = gateway.Generated("InProcessProgramRegistrations.g.cs");
        registrations.ShouldContain("Register(\"Web\", \"orders-api\", global::Assimalign.Cohesion.Generated.Shop.Orders.InProcessEntryPoint.RunAsync);", Case.Sensitive);
        registrations.ShouldContain("Register(\"Web\", \"billing-api\", global::Assimalign.Cohesion.Generated.Shop.Billing.InProcessEntryPoint.RunAsync);", Case.Sensitive);
        gateway.Emit().ShouldNotBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource reference to an assembly with no program is reported and not registered")]
    public void Catalog_WithReferenceWithoutProgram_ShouldReportIt()
    {
        // Arrange
        byte[] library = CompileImage("Shop.Library", "namespace Shop.Library; public static class Thing { }", OutputKind.DynamicallyLinkedLibrary, _hosting);

        (string Path, Dictionary<string, string> Metadata) reference = Resource("Shop.Library", "orders-api");

        // Act
        GeneratorRun gateway = RunGateway([("Shop.Library", library)], reference);

        // Assert: the diagnostic points at the referenced project, so the build output names the file.
        Diagnostic diagnostic = gateway.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("COHAPM0001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().ShouldContain("'Shop.Library'", Case.Sensitive);
        diagnostic.Location.GetLineSpan().Path.ShouldBe(reference.Path);
        gateway.RunResult.GeneratedTrees.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource reference whose assembly the gateway does not reference is reported and not registered")]
    public void Catalog_WithUnreferencedAssembly_ShouldReportIt()
    {
        // Arrange: the build hands over a reference with no resolved assembly, as it does for
        // ReferenceOutputAssembly="false".
        byte[] orders = CompileResource("Shop.Orders", "System.Console.WriteLine();");
        (string Path, Dictionary<string, string> Metadata) reference = Resource("Shop.Orders", "orders-api");
        reference.Metadata.Remove("CohesionResourceAssembly");

        // Act
        GeneratorRun gateway = RunGateway([("Shop.Orders", orders)], reference);

        // Assert
        Diagnostic diagnostic = gateway.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("COHAPM0001");
        diagnostic.GetMessage().ShouldContain("'Shop.Orders.csproj'", Case.Sensitive);
        diagnostic.GetMessage().ShouldContain("ReferenceOutputAssembly", Case.Sensitive);
        diagnostic.Location.GetLineSpan().Path.ShouldBe(reference.Path);
        gateway.RunResult.GeneratedTrees.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource whose program records no provider is reported, unless the reference names one")]
    public void Catalog_WithoutProvider_ShouldReportItUnlessTheReferenceNamesOne()
    {
        // Arrange: the program references no area integration, so its build could not tell its provider.
        byte[] orders = CompileResource("Shop.Orders", "System.Console.WriteLine();", includeArea: false);

        // Act
        GeneratorRun missing = RunGateway([("Shop.Orders", orders)], Resource("Shop.Orders", "orders-api"));
        GeneratorRun overridden = RunGateway([("Shop.Orders", orders)], Resource("Shop.Orders", "orders-api", provider: "Custom"));

        // Assert
        Diagnostic diagnostic = missing.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("COHAPM0002");
        diagnostic.GetMessage().ShouldContain("CohesionProvider", Case.Sensitive);
        missing.RunResult.GeneratedTrees.ShouldBeEmpty();
        overridden.Diagnostics.ShouldBeEmpty();
        overridden.Generated("InProcessProgramRegistrations.g.cs").ShouldContain("Register(\"Custom\", \"orders-api\",", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A provider on the reference overrides the one the program recorded")]
    public void Catalog_WithProviderOnReference_ShouldOverrideTheRecordedOne()
    {
        // Arrange
        byte[] orders = CompileResource("Shop.Orders", "System.Console.WriteLine();");

        // Act
        GeneratorRun gateway = RunGateway([("Shop.Orders", orders)], Resource("Shop.Orders", "orders-api", provider: "Storefront"));

        // Assert
        gateway.Diagnostics.ShouldBeEmpty();
        string registrations = gateway.Generated("InProcessProgramRegistrations.g.cs");
        registrations.ShouldContain("Register(\"Storefront\", \"orders-api\",", Case.Sensitive);
        registrations.ShouldNotContain("\"Web\"", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Two references that name the same resource are an error, and the second is not registered")]
    public void Catalog_WithDuplicateResourceNames_ShouldReportAnError()
    {
        // Arrange
        byte[] orders = CompileResource("Shop.Orders", "System.Console.WriteLine();");
        byte[] billing = CompileResource("Shop.Billing", "System.Console.WriteLine();");

        // Act
        GeneratorRun gateway = RunGateway(
            [("Shop.Orders", orders), ("Shop.Billing", billing)],
            Resource("Shop.Orders", "orders-api"),
            Resource("Shop.Billing", "orders-api"));

        // Assert: sorted by assembly name, Shop.Billing binds first.
        Diagnostic diagnostic = gateway.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("COHAPM0003");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("'Shop.Billing' and 'Shop.Orders'", Case.Sensitive);
        string registrations = gateway.Generated("InProcessProgramRegistrations.g.cs");
        registrations.ShouldContain("Shop.Billing.InProcessEntryPoint.RunAsync", Case.Sensitive);
        registrations.ShouldNotContain("Shop.Orders.InProcessEntryPoint", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Two references whose providers differ only in case name the same resource, which is an error")]
    public void Catalog_WithDuplicateResourceNamesDifferingInProviderCase_ShouldReportAnError()
    {
        // Arrange: the catalog compares provider names without regard to case, so "Web" and "web"
        // would bind one resource twice and fail at the gateway's module load.
        byte[] orders = CompileResource("Shop.Orders", "System.Console.WriteLine();");
        byte[] billing = CompileResource("Shop.Billing", "System.Console.WriteLine();");

        // Act
        GeneratorRun gateway = RunGateway(
            [("Shop.Orders", orders), ("Shop.Billing", billing)],
            Resource("Shop.Orders", "orders-api"),
            Resource("Shop.Billing", "orders-api", provider: "web"));

        // Assert
        Diagnostic diagnostic = gateway.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("COHAPM0003");
        diagnostic.GetMessage().ShouldContain("'Shop.Billing' and 'Shop.Orders'", Case.Sensitive);
        string registrations = gateway.Generated("InProcessProgramRegistrations.g.cs");
        registrations.ShouldContain("Register(\"web\", \"orders-api\", global::Assimalign.Cohesion.Generated.Shop.Billing.InProcessEntryPoint.RunAsync);", Case.Sensitive);
        registrations.ShouldNotContain("Shop.Orders.InProcessEntryPoint", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A compilation without the in-process gateway package registers nothing")]
    public void Catalog_WithoutGatewayInProcess_ShouldEmitNothing()
    {
        // Arrange
        byte[] orders = CompileResource("Shop.Orders", "System.Console.WriteLine();");

        // Act
        GeneratorRun gateway = RunGateway([("Shop.Orders", orders)], [Resource("Shop.Orders", "orders-api")], includeCatalog: false);

        // Assert
        gateway.Diagnostics.ShouldBeEmpty();
        gateway.RunResult.GeneratedTrees.ShouldBeEmpty();
    }

    private static (string Path, Dictionary<string, string> Metadata) Resource(string assembly, string resource, string? provider = null)
    {
        var metadata = new Dictionary<string, string>
        {
            ["CohesionResource"] = resource,
            ["CohesionResourceAssembly"] = assembly,
        };
        if (provider is not null)
        {
            metadata["CohesionProvider"] = provider;
        }

        return (Path.Combine(Path.GetTempPath(), "gateway-tests", assembly, assembly + ".csproj"), metadata);
    }

    private static GeneratorRun RunResource(string assemblyName, string source)
    {
        GeneratorRun run = Run(assemblyName, source, OutputKind.ConsoleApplication, [_hosting, _webIntegration, _probe]);
        run.AssertNoErrors();
        return run;
    }

    private static byte[] CompileResource(string assemblyName, string source, bool includeArea = true)
    {
        GeneratorRun run = Run(assemblyName, source, OutputKind.ConsoleApplication, includeArea ? [_hosting, _webIntegration, _probe] : [_hosting, _probe]);
        run.Generated("InProcessEntryPoint.g.cs").ShouldNotBeEmpty();
        return run.Emit();
    }

    private static GeneratorRun RunGateway(
        IEnumerable<(string Name, byte[] Image)> resources,
        params (string Path, Dictionary<string, string> Metadata)[] references) =>
        RunGateway(resources, references, includeCatalog: true);

    private static GeneratorRun RunGateway(
        IEnumerable<(string Name, byte[] Image)> resources,
        IEnumerable<(string Path, Dictionary<string, string> Metadata)> references,
        bool includeCatalog)
    {
        var options = new TestAnalyzerConfigOptionsProvider();
        var files = new List<AdditionalText>();
        foreach ((string path, Dictionary<string, string> metadata) in references)
        {
            options.Add(path, metadata);
            files.Add(new TestAdditionalText(path));
        }

        List<byte[]> images = [_hosting, _gateway, _probe, .. resources.Select(resource => resource.Image)];
        if (includeCatalog)
        {
            images.Add(_gatewayInProcess);
        }

        GeneratorRun run = Run("Shop.Gateway", "System.Console.WriteLine(args.Length);", OutputKind.ConsoleApplication, images, options, files);
        run.AssertNoErrors();
        return run;
    }

    private static GeneratorRun Run(
        string assemblyName,
        string source,
        OutputKind outputKind,
        IEnumerable<byte[]> references,
        TestAnalyzerConfigOptionsProvider? options = null,
        IEnumerable<AdditionalText>? additionalFiles = null)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, _parseOptions)],
            [.. _platformReferences, .. references.Select(image => MetadataReference.CreateFromImage(image))],
            new CSharpCompilationOptions(outputKind, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new InProcessProgramBridgeGenerator().AsSourceGenerator(), new InProcessProgramCatalogGenerator().AsSourceGenerator()],
            additionalFiles,
            _parseOptions,
            options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out _);
        return new GeneratorRun((CSharpCompilation)updated, driver.GetRunResult());
    }

    private static byte[] CompileImage(string assemblyName, string source, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary, params byte[][] references)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, _parseOptions)],
            [.. _platformReferences, .. references.Select(image => MetadataReference.CreateFromImage(image))],
            new CSharpCompilationOptions(outputKind, nullableContextOptions: NullableContextOptions.Enable));
        return EmitImage(compilation);
    }

    private static byte[] EmitImage(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Success.ShouldBeTrue(FormatDiagnostics(result.Diagnostics));
        return stream.ToArray();
    }

    // Loads the resource and calls its bridge as a gateway's catalog would, then reads what it recorded.
    private static async Task<string[]> InvokeBridgeAsync(GeneratorRun run, string[] args)
    {
        var context = new AssemblyLoadContext(nameof(InProcessProgramGeneratorTests), isCollectible: true);
        try
        {
            string name = run.Compilation.AssemblyName!;
            Func<string, Assembly> load = CreateLoader(context, new Dictionary<string, byte[]> { [name] = run.Emit() });
            Type bridge = load(name).GetType(InProcessProgramBridgeGenerator.GetBridgeNamespace(name) + ".InProcessEntryPoint", throwOnError: true)!;
            var task = (Task)bridge.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [args])!;
            await task;
            return ReadProbe(load);
        }
        finally
        {
            context.Unload();
        }
    }

    private static Func<string, Assembly> CreateLoader(AssemblyLoadContext context, Dictionary<string, byte[]> images)
    {
        var all = new Dictionary<string, byte[]>(images, StringComparer.Ordinal)
        {
            [HostingAssembly] = _hosting,
            [WebIntegrationAssembly] = _webIntegration,
            [ProbeAssembly] = _probe,
        };
        var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        Assembly Load(string name)
        {
            if (!loaded.TryGetValue(name, out Assembly? assembly))
            {
                assembly = context.LoadFromStream(new MemoryStream(all[name]));
                loaded[name] = assembly;
            }

            return assembly;
        }

        context.Resolving += (_, name) => name.Name is not null && all.ContainsKey(name.Name) ? Load(name.Name) : null;
        return Load;
    }

    private static string[] ReadProbe(Func<string, Assembly> load)
    {
        var calls = (List<string>)load(ProbeAssembly).GetType("Test.Probe.Probe", throwOnError: true)!.GetProperty("Calls")!.GetValue(null)!;
        return [.. calls];
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.ToString()));

    private readonly record struct GeneratorRun(CSharpCompilation Compilation, GeneratorDriverRunResult RunResult)
    {
        public IReadOnlyList<Diagnostic> Diagnostics => RunResult.Diagnostics;

        public string Generated(string hintName) =>
            RunResult.Results.SelectMany(result => result.GeneratedSources)
                .Single(source => source.HintName == hintName).SourceText.ToString();

        public void AssertNoErrors()
        {
            Diagnostic[] errors = Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            errors.ShouldBeEmpty(FormatDiagnostics(errors));
        }

        public byte[] Emit() => EmitImage(Compilation);
    }
}
