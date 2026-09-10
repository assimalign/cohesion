using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[Collection(ConsoleOutputCollection.Name)]
public class ApplicationSetTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set describe composes every member model in declaration order")]
    public async Task RunAsync_DescribeWithTwoMembers_ShouldWriteComposedModelArray()
    {
        // Arrange
        IApplicationModel first = CreateModel(ApplicationName.Parse("platform"));
        IApplicationModel second = CreateModel(ApplicationName.Parse("appa"));
        var gateway = new RecordingMultiModelGateway("set-gateway");
        IApplicationSet set = Application.CreateSet(
                gateway,
                ["--mode=describe", "--gateway=set-gateway", "--environment=Development"])
            .AddApplication(new ApplicationDeclaration(
                first.Name,
                new RecordingResolver(first.Name, first, new List<ApplicationName>())))
            .AddApplication(new ApplicationDeclaration(
                second.Name,
                new RecordingResolver(second.Name, second, new List<ApplicationName>())));
        TextWriter original = Console.Out;
        using var output = new StringWriter();

        try
        {
            Console.SetOut(output);

            // Act
            await set.RunAsync();

            // Assert
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            JsonElement root = document.RootElement;
            root.ValueKind.ShouldBe(JsonValueKind.Array);
            root.GetArrayLength().ShouldBe(2);
            root[0].GetProperty("application").GetString().ShouldBe("platform");
            root[1].GetProperty("application").GetString().ShouldBe("appa");
            gateway.Calls.ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set render dispatches the complete model collection to the gateway")]
    public async Task RunAsync_RenderWithTwoMembers_ShouldDispatchOneOrderedRender()
    {
        // Arrange
        IApplicationModel first = CreateModel(ApplicationName.Parse("platform"));
        IApplicationModel second = CreateModel(ApplicationName.Parse("appa"));
        var gateway = new RecordingMultiModelGateway("set-gateway");
        IApplicationSet set = Application.CreateSet(
                gateway,
                ["--mode=render", "--gateway=set-gateway", "--environment=Development"])
            .AddApplication(new ApplicationDeclaration(
                first.Name,
                new RecordingResolver(first.Name, first, new List<ApplicationName>())))
            .AddApplication(new ApplicationDeclaration(
                second.Name,
                new RecordingResolver(second.Name, second, new List<ApplicationName>())));
        TextWriter original = Console.Out;
        using var output = new StringWriter();

        try
        {
            Console.SetOut(output);

            // Act
            await set.RunAsync();

            // Assert
            gateway.Calls.ShouldBe(["validate-batch", "render-batch"]);
            gateway.RenderedModels.ShouldNotBeNull();
            gateway.RenderedModels![0].ShouldBeSameAs(first);
            gateway.RenderedModels[1].ShouldBeSameAs(second);
            output.ToString().ShouldBe("rendered" + Environment.NewLine);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set external binds directly to an in-memory sibling before fallback")]
    public async Task RunAsync_ExternalTargetsSibling_ShouldPreferDirectSetResolution()
    {
        // Arrange
        IApplicationModel provider = CreateModel(ApplicationName.Parse("platform"));
        ResourceManifest manifest = provider.Manifests[0];
        var fallback = new RecordingExternalResolver(
            ExternalResourceResolution.Unresolved("Remote control plane should not be used."));
        IApplicationModel consumer = CreateExternalModel(
            ApplicationName.Parse("appa"),
            provider.Name,
            manifest,
            fallback);
        var gateway = new RecordingMultiModelGateway("set-gateway")
        {
            DirectResolution = new ExternalResourceResolution(
                true,
                [new ResourceEndpoint("control", "http", 5042, Host: "127.0.0.1")],
                ResourceManifestCanonicalizer.ComputeHash(manifest),
                ApplicationExportDocument.CurrentSchemaVersion,
                "in-set: direct"),
        };
        IApplicationSet set = Application.CreateSet(
                gateway,
                ["--mode=apply", "--gateway=set-gateway", "--environment=Development"])
            .AddApplication(new ApplicationDeclaration(
                provider.Name,
                new RecordingResolver(provider.Name, provider, new List<ApplicationName>())))
            .AddApplication(new ApplicationDeclaration(
                consumer.Name,
                new RecordingResolver(consumer.Name, consumer, new List<ApplicationName>())));

        // Act
        await set.RunAsync();
        IExternalResource external = gateway.ReconciledModels![1].Resources[0]
            .ShouldBeAssignableTo<IExternalResource>();
        ExternalResourceResolution resolution = await external.Resolver.ResolveAsync(
            new ExternalResourceResolutionContext(external.Declaration));

        // Assert
        resolution.Resolved.ShouldBeTrue();
        resolution.Endpoints[0].Port.ShouldBe(5042);
        gateway.DirectResolutionCount.ShouldBe(1);
        fallback.CallCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set realize accepts the owning exported model and ignores unrelated members")]
    public async Task RunAsync_RealizedExternalAcrossExportedMembers_ShouldValidateOnceAcrossSet()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-application-set-realize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ownerPath = Path.Combine(root, "owner.json");
        string unrelatedPath = Path.Combine(root, "unrelated.json");
        IApplicationModel owner = CreateRealizedExternalModel();
        IApplicationModel unrelated = CreateModel(ApplicationName.Parse("unrelated"));
        ApplicationExportDocument.Create(owner, "1.0.0").Save(ownerPath);
        ApplicationExportDocument.Create(unrelated, "1.0.0").Save(unrelatedPath);
        var gateway = new RecordingMultiModelGateway("local");
        IApplicationSet set = Application.CreateSet(
                gateway,
                [
                    "--mode=apply",
                    "--gateway=local",
                    "--environment=Development",
                    "--realize=peer-api",
                ])
            .AddApplication(new ApplicationDeclaration(
                owner.Name,
                ApplicationModelResolvers.File(ownerPath)))
            .AddApplication(new ApplicationDeclaration(
                unrelated.Name,
                ApplicationModelResolvers.File(unrelatedPath)));

        try
        {
            // Act
            await set.RunAsync();

            // Assert
            gateway.Calls.ShouldBe(["validate-batch", "reconcile-batch"]);
            gateway.ReconciledModels.ShouldNotBeNull();
            gateway.ReconciledModels![0].Resources[0]
                .ShouldBeAssignableTo<IExternalResource>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Apply resolves declarations at run start and reconciles one ordered batch")]
    public async Task RunAsync_ApplyWithTwoDeclarations_ResolvesAtStartAndReconcilesTogetherInOrder()
    {
        // Arrange
        ApplicationName firstName = ApplicationName.Parse("identity");
        ApplicationName secondName = ApplicationName.Parse("erp");
        IApplicationModel firstModel = CreateModel(firstName);
        IApplicationModel secondModel = CreateModel(secondName);
        var resolutionOrder = new List<ApplicationName>();
        var firstResolver = new RecordingResolver(firstName, firstModel, resolutionOrder);
        var secondResolver = new RecordingResolver(secondName, secondModel, resolutionOrder);
        var firstDeclaration = new ApplicationDeclaration(firstName, firstResolver);
        var secondDeclaration = new ApplicationDeclaration(secondName, secondResolver);
        var gateway = new RecordingMultiModelGateway("set-gateway");
        IApplicationSet applicationSet = Application.CreateSet(
                gateway,
                ["--mode=apply", "--gateway=set-gateway", "--environment=Development"])
            .AddApplication(firstDeclaration)
            .AddApplication(secondDeclaration);

        firstDeclaration.Name.ShouldBe(firstName);
        firstDeclaration.Resolver.ShouldBeSameAs(firstResolver);
        secondDeclaration.Name.ShouldBe(secondName);
        secondDeclaration.Resolver.ShouldBeSameAs(secondResolver);
        firstResolver.CallCount.ShouldBe(0);
        secondResolver.CallCount.ShouldBe(0);
        resolutionOrder.ShouldBeEmpty();
        gateway.Calls.ShouldBeEmpty();

        // Act
        await applicationSet.RunAsync();

        // Assert
        resolutionOrder.ShouldBe(new[] { firstName, secondName });
        firstResolver.CallCount.ShouldBe(1);
        secondResolver.CallCount.ShouldBe(1);
        firstResolver.LastContext.ShouldNotBeNull();
        secondResolver.LastContext.ShouldBeSameAs(firstResolver.LastContext);
        firstResolver.LastContext.RunMode.ShouldBe(GatewayRunMode.Apply);
        firstResolver.LastContext.GatewayIdentity.ShouldBe((ResourceName)"set-gateway");
        firstResolver.LastContext.Environment.IsDevelopment.ShouldBeTrue();

        gateway.Calls.ShouldBe(new[] { "validate-batch", "reconcile-batch" });
        gateway.BatchValidationCount.ShouldBe(1);
        gateway.BatchReconcileCount.ShouldBe(1);
        gateway.SingleModelCallCount.ShouldBe(0);
        IReadOnlyList<IApplicationModel> validatedModels = gateway.ValidatedModels.ShouldNotBeNull();
        IReadOnlyList<IApplicationModel> reconciledModels = gateway.ReconciledModels.ShouldNotBeNull();
        reconciledModels.ShouldBeSameAs(validatedModels);
        reconciledModels.Count.ShouldBe(2);
        reconciledModels[0].ShouldBeSameAs(firstModel);
        reconciledModels[1].ShouldBeSameAs(secondModel);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set rejects a resolver model for the wrong application")]
    public async Task RunAsync_ResolverReturnsDifferentApplication_ThrowsBeforeGatewayContact()
    {
        // Arrange
        ApplicationName declaredName = ApplicationName.Parse("expected");
        IApplicationModel wrongModel = CreateModel(ApplicationName.Parse("actual"));
        var resolver = new RecordingResolver(
            declaredName,
            wrongModel,
            new List<ApplicationName>());
        var gateway = new RecordingMultiModelGateway("set-gateway");
        IApplicationSet applicationSet = Application.CreateSet(
                gateway,
                ["--mode=apply", "--gateway=set-gateway", "--environment=Development"])
            .AddApplication(new ApplicationDeclaration(declaredName, resolver));

        // Act
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            () => applicationSet.RunAsync());

        // Assert
        resolver.CallCount.ShouldBe(1);
        error.Message.ShouldContain("expected", Case.Sensitive);
        error.Message.ShouldContain("actual", Case.Sensitive);
        error.Message.ShouldContain("wrong application", Case.Sensitive);
        gateway.Calls.ShouldBeEmpty();
        gateway.BatchValidationCount.ShouldBe(0);
        gateway.BatchReconcileCount.ShouldBe(0);
        gateway.SingleModelCallCount.ShouldBe(0);
    }

    private static IApplicationModel CreateModel(ApplicationName name)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                name,
                ["--mode=apply", "--gateway=fake", "--environment=Development"])
            .UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("consumer-worker"));
        return builder.Build().Model;
    }

    private static IApplicationModel CreateRealizedExternalModel()
    {
        ResourceManifest manifest = TestManifestFactory.Create("peer-api", "peer");
        var declaration = new ExternalResourceDeclaration(
            "peer-api",
            "peer",
            ["control"],
            optional: false,
            manifest,
            [manifest]);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("owner"),
                [
                    "--mode=apply",
                    "--environment=Development",
                    "--realize=peer-api",
                ])
            .UseGateway(new FakeGateway("local"));
        builder.AddExternal(declaration);
        return builder.Build().Model;
    }

    private static IApplicationModel CreateExternalModel(
        ApplicationName name,
        ApplicationName targetApplication,
        ResourceManifest manifest,
        IExternalResourceResolver resolver)
    {
        var declaration = new ExternalResourceDeclaration(
            manifest.Name,
            targetApplication,
            ["control"],
            optional: false,
            manifest,
            [manifest]);
        IApplicationBuilder builder = Application.CreateBuilder(
                name,
                ["--mode=apply", "--gateway=fake", "--environment=Development"])
            .UseGateway(new FakeGateway());
        builder.AddExternal(declaration, resolver);
        builder.AddResource(new FakeResource("worker"));
        return builder.Build().Model;
    }

    private sealed class RecordingResolver : IApplicationModelResolver
    {
        private readonly ApplicationName _declarationName;
        private readonly IApplicationModel _model;
        private readonly ICollection<ApplicationName> _resolutionOrder;

        public RecordingResolver(
            ApplicationName declarationName,
            IApplicationModel model,
            ICollection<ApplicationName> resolutionOrder)
        {
            _declarationName = declarationName;
            _model = model;
            _resolutionOrder = resolutionOrder;
        }

        public int CallCount { get; private set; }

        public ApplicationModelResolutionContext? LastContext { get; private set; }

        public ValueTask<IApplicationModel> ResolveAsync(
            ApplicationModelResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastContext = context;
            _resolutionOrder.Add(_declarationName);
            return ValueTask.FromResult(_model);
        }
    }

    private sealed class RecordingMultiModelGateway :
        IMultiModelApplicationGateway,
        IApplicationGatewayRenderer,
        IApplicationSetExternalResourceResolver
    {
        public RecordingMultiModelGateway(string name)
        {
            Name = name;
        }

        public ResourceName Name { get; }

        public List<string> Calls { get; } = new();

        public int BatchValidationCount { get; private set; }

        public int BatchReconcileCount { get; private set; }

        public int SingleModelCallCount { get; private set; }

        public IReadOnlyList<IApplicationModel>? ValidatedModels { get; private set; }

        public IReadOnlyList<IApplicationModel>? ReconciledModels { get; private set; }

        public IReadOnlyList<IApplicationModel>? RenderedModels { get; private set; }

        public ExternalResourceResolution DirectResolution { get; init; } =
            ExternalResourceResolution.Unresolved("No direct resolution configured.");

        public int DirectResolutionCount { get; private set; }

        public void Validate(IApplicationModel model)
        {
            SingleModelCallCount++;
        }

        public void Validate(IReadOnlyList<IApplicationModel> models)
        {
            Calls.Add("validate-batch");
            BatchValidationCount++;
            ValidatedModels = models;
        }

        public Task StartAsync(
            IApplicationModel model,
            CancellationToken cancellationToken = default)
        {
            SingleModelCallCount++;
            return Task.CompletedTask;
        }

        public Task StartAsync(
            IReadOnlyList<IApplicationModel> models,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("start-batch");
            return Task.CompletedTask;
        }

        public Task ReconcileAsync(
            IApplicationModel model,
            CancellationToken cancellationToken = default)
        {
            SingleModelCallCount++;
            return Task.CompletedTask;
        }

        public Task ReconcileAsync(
            IReadOnlyList<IApplicationModel> models,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("reconcile-batch");
            BatchReconcileCount++;
            ReconciledModels = models;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task UninstallAsync(
            IApplicationModel model,
            CancellationToken cancellationToken = default)
        {
            SingleModelCallCount++;
            return Task.CompletedTask;
        }

        public Task UninstallAsync(
            IReadOnlyList<IApplicationModel> models,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("uninstall-batch");
            return Task.CompletedTask;
        }

        public async Task RenderAsync(
            IReadOnlyList<IApplicationModel> models,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("render-batch");
            RenderedModels = models;
            await output.WriteLineAsync("rendered");
        }

        public ValueTask<ExternalResourceResolution> ResolveInSetAsync(
            IReadOnlyList<IApplicationModel> models,
            ExternalResourceResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            DirectResolutionCount++;
            return ValueTask.FromResult(DirectResolution);
        }
    }

    private sealed class RecordingExternalResolver : IExternalResourceResolver
    {
        private readonly ExternalResourceResolution _resolution;

        public RecordingExternalResolver(ExternalResourceResolution resolution)
        {
            _resolution = resolution;
        }

        public int CallCount { get; private set; }

        public ValueTask<ExternalResourceResolution> ResolveAsync(
            ExternalResourceResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_resolution);
        }
    }
}
