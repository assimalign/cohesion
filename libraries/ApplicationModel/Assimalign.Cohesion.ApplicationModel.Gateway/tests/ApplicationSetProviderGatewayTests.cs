using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Application-set members imported from their describe output carry no providers. The set attaches
/// each member's own registrations from its <c>AddApplication(..., configure)</c> callback, and the
/// gateway resolves that member's store mounts through them exactly as for a builder-built model.
/// </summary>
[Collection(LocalGatewayConsoleCollection.Name)]
public sealed class ApplicationSetProviderGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Application set providers: each imported member resolves its store mounts through its own registrations")]
    public async Task ApplicationSet_ImportedMembersWithRegistrations_ShouldResolveThroughEachMembersProviders()
    {
        // Arrange
        var controller = new MemberStoreController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions());
        IApplicationModel appa = ImportMember("appa");
        IApplicationModel appb = ImportMember("appb");
        var appaSecrets = new RecordingSourceProvider("KeyStore") { Secret = Encoding.UTF8.GetBytes("appa-secret") };
        var appbSecrets = new RecordingSourceProvider("KeyStore") { Secret = Encoding.UTF8.GetBytes("appb-secret") };
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(appa), member => member.Providers.Sources["secrets"] = appaSecrets)
            .AddApplication(Declare(appb), member => member.Providers.Sources["secrets"] = appbSecrets);

        try
        {
            // Act
            await set.RunAsync();

            // Assert
            Encoding.UTF8.GetString(controller.ConsumerInputs["appa"].Mounts["api-key"].Content.Span)
                .ShouldBe("appa-secret");
            Encoding.UTF8.GetString(controller.ConsumerInputs["appb"].Mounts["api-key"].Content.Span)
                .ShouldBe("appb-secret");

            ResourceSourceRequest appaRequest = appaSecrets.Requests.ShouldHaveSingleItem();
            appaRequest.Application.ShouldBe(ApplicationName.Parse("appa"));
            appaRequest.Consumer.ShouldBe((ResourceName)"api");
            appaRequest.Key.ShouldBe("api-key");
            ResourceProviderConnection appaStore = appaRequest.Store.ShouldNotBeNull();
            appaStore.Caller.ShouldBe(ApplicationName.Parse("appa"));
            appaStore.Resource.ShouldBe((ResourceName)"secrets");
            appaStore.ControlPlaneAddress.ShouldBe(new Uri("http://127.0.0.1:5201/cohesion/v1"));
            JsonWebToken.Parse(appaStore.BearerCredential).Audiences.ShouldBe(["secrets"]);

            ResourceSourceRequest appbRequest = appbSecrets.Requests.ShouldHaveSingleItem();
            appbRequest.Application.ShouldBe(ApplicationName.Parse("appb"));
            ResourceProviderConnection appbStore = appbRequest.Store.ShouldNotBeNull();
            appbStore.Caller.ShouldBe(ApplicationName.Parse("appb"));
            appbStore.ControlPlaneAddress.ShouldBe(new Uri("http://127.0.0.1:5202/cohesion/v1"));
        }
        finally
        {
            await ((IMultiModelApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Application set providers: an unregistered store-backed member fails before the gateway realizes anything")]
    public async Task ApplicationSet_UnregisteredStoreBackedMember_ShouldFailBeforeRealization()
    {
        // Arrange
        var controller = new MemberStoreController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions());
        var appaSecrets = new RecordingSourceProvider("KeyStore");
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), member => member.Providers.Sources["secrets"] = appaSecrets)
            .AddApplication(Declare(ImportMember("appb")));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appb' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("no provider is registered for source 'secrets'", Case.Sensitive);
        exception.Message.ShouldContain("application => application.UseKeyStore(\"secrets\")", Case.Sensitive);
        controller.Reconciled.ShouldBeEmpty();
        appaSecrets.Requests.ShouldBeEmpty();
    }

    private static ApplicationDeclaration Declare(IApplicationModel model) =>
        new(model.Name, new FixedModelResolver(model));

    // The member's own gateway builds it with its registrations; its describe output, which the set
    // imports, carries none.
    private static IApplicationModel ImportMember(string application)
    {
        var memberGateway = new TestGateway(new InMemoryResourceStateManager(), [new MemberStoreController()]);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(application),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(memberGateway);
        IApplicationResourceDescriptor store = builder.AddResource(CreateManifest(application, "secrets", "KeyStore"));
        builder.AddResource(CreateManifest(
                application,
                "api",
                "Web",
                mounts:
                [
                    new ResourceManifestMount
                    {
                        Name = "api-key",
                        ContainerPath = "/inputs/api-key",
                        Kind = ResourceMountKind.Secret,
                        Source = "secrets:api-key",
                    },
                ],
                references: ["secrets"]))
            .DependsOn(store);
        builder.Providers.Sources["secrets"] = new RecordingSourceProvider("KeyStore");
        IApplicationModel imported = ApplicationModelDocument.Create(builder.Build().Model)
            .ToModel(GatewayRunMode.Apply, (ResourceName)"test");
        imported.Providers.ShouldBeSameAs(ApplicationProviders.Empty);
        return imported;
    }

    private static ApplicationGatewayOptions CreateOptions() => new()
    {
        ExportDirectory = Path.Combine(
            Path.GetTempPath(),
            "cohesion-gateway-set-provider-tests",
            Guid.NewGuid().ToString("N")),
    };

    private static ResourceManifest CreateManifest(
        string application,
        string name,
        string kind,
        IReadOnlyList<ResourceManifestMount>? mounts = null,
        IReadOnlyList<string>? references = null)
    {
        var manifestReferences = new List<ResourceManifestReference>();
        foreach (string resource in references ?? Array.Empty<string>())
        {
            manifestReferences.Add(new ResourceManifestReference
            {
                Resource = resource,
                Application = application,
                Endpoints = ["api"],
                Manifest = "Assimalign.Cohesion.Test.ApplicationModel",
            });
        }

        return new ResourceManifest
        {
            Name = name,
            Application = application,
            Kind = kind,
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "test.dll",
                AppHost = "test",
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "api",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "api",
                Path = "/cohesion/v1",
            },
            Mounts = mounts ?? Array.Empty<ResourceManifestMount>(),
            References = manifestReferences,
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                Replicas = 1,
                RestartPolicy = "Never",
            },
        };
    }

    private sealed class FixedModelResolver : IApplicationModelResolver
    {
        private readonly IApplicationModel _model;

        public FixedModelResolver(IApplicationModel model)
        {
            _model = model;
        }

        public ValueTask<IApplicationModel> ResolveAsync(
            ApplicationModelResolutionContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_model);
    }

    // Marks each member's store Running on its own loopback port and records the inputs the gateway
    // resolved for each member's consumer.
    private sealed class MemberStoreController : IApplicationResourceController
    {
        public Dictionary<string, ResourceInputs> ConsumerInputs { get; } = new(StringComparer.Ordinal);

        public List<string> Reconciled { get; } = new();

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string application = context.Model.Name.ToString();
            Reconciled.Add($"{application}/{context.Resource.Name}");
            if (context.Resource.Name == (ResourceName)"secrets")
            {
                int port = application == "appa" ? 5201 : 5202;
                context.State.SetState(
                    context.Resource.Id,
                    ResourceLifecycle.Running,
                    observedEndpoints: [new ResourceEndpoint("api", "http", port, Host: "127.0.0.1")]);
            }
            else
            {
                ConsumerInputs[application] = context.Inputs;
                context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
