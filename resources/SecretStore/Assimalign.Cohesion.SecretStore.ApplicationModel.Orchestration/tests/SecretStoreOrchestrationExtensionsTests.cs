using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

public sealed class SecretStoreOrchestrationExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Registers the store's source provider and one add-secret resolver")]
    public void UseSecretStore_WithSecretStore_ShouldRegisterSourceAndResolver()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");

        // Act
        SecretStoreProviderBuilder handle = builder.UseSecretStore(store);

        // Assert
        handle.Providers.ShouldBeSameAs(builder.Providers);
        handle.Store.ShouldBe((ResourceName)"secrets");
        builder.Providers.Sources.Keys.ShouldBe(["secrets"]);
        builder.Providers.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
        builder.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeOfType<SecretStoreAddSecretInputResolver>();
        builder.Providers.CertificateAuthority.ShouldBeNull();
        builder.Providers.TrustStore.ShouldBeNull();
        builder.Providers.Telemetry.ShouldBeNull();
        builder.Providers.CredentialIssuer.ShouldBeNull();
        builder.Providers.Callers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Registering the same store again changes nothing")]
    public void UseSecretStore_CalledTwiceForSameStore_ShouldBeIdempotent()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");
        builder.UseSecretStore(store);
        IResourceSourceProvider first = builder.Providers.Sources["secrets"];
        IResourceCommandInputResolver resolver = builder.Providers.CommandInputs[0];

        // Act
        SecretStoreProviderBuilder handle = builder.UseSecretStore(store);

        // Assert
        handle.Store.ShouldBe((ResourceName)"secrets");
        builder.Providers.Sources["secrets"].ShouldBeSameAs(first);
        builder.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeSameAs(resolver);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Two stores get two sources and share one resolver")]
    public void UseSecretStore_WithTwoStores_ShouldRegisterBothSourcesAndOneResolver()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor first = AddStore(builder, "secrets");
        IApplicationResourceDescriptor second = AddStore(builder, "vault");

        // Act
        builder.UseSecretStore(first);
        builder.UseSecretStore(second);

        // Assert
        builder.Providers.Sources.Count.ShouldBe(2);
        builder.Providers.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
        builder.Providers.Sources["vault"].ShouldBeOfType<SecretStoreSourceProvider>();
        builder.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeOfType<SecretStoreAddSecretInputResolver>();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: A resource that is not manifest-backed is accepted; Build checks its kind")]
    public void UseSecretStore_WithResourceWithoutManifest_ShouldRegister()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(new TestResource("secrets"));

        // Act
        builder.UseSecretStore(store);

        // Assert
        builder.Providers.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Rejects a manifest-backed resource of another kind")]
    public void UseSecretStore_WithConfigurationStoreManifest_ShouldThrowArgumentException()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor configuration = AddStore(builder, "settings", "ConfigurationStore");

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => builder.UseSecretStore(configuration));

        // Assert
        exception.ParamName.ShouldBe("store");
        exception.Message.ShouldContain("kind 'ConfigurationStore'", Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
        builder.Providers.CommandInputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Accepts the SecretStore manifest kind case-insensitively")]
    public void UseSecretStore_WithLowercaseManifestKind_ShouldRegister()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets", "secretstore");

        // Act
        builder.UseSecretStore(store);

        // Assert
        builder.Providers.Sources.ContainsKey("secrets").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Rejects null arguments")]
    public void UseSecretStore_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");

        // Act
        ArgumentNullException missingStore = Should.Throw<ArgumentNullException>(() => builder.UseSecretStore(null!));
        ArgumentNullException missingBuilder = Should.Throw<ArgumentNullException>(
            () => SecretStoreOrchestrationExtensions.UseSecretStore(null!, store));

        // Assert
        missingStore.ParamName.ShouldBe("store");
        missingBuilder.ParamName.ShouldBe("builder");
        builder.Providers.Sources.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Never replaces another provider registered under the store's name")]
    public void UseSecretStore_WhenSourceHasForeignProvider_ShouldThrowAndChangeNothing()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");
        var foreign = new ForeignSourceProvider();
        builder.Providers.Sources["secrets"] = foreign;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.UseSecretStore(store));

        // Assert
        exception.Message.ShouldContain(nameof(ForeignSourceProvider), Case.Sensitive);
        builder.Providers.Sources["secrets"].ShouldBeSameAs(foreign);
        builder.Providers.CommandInputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Never replaces another secretstore.add-secret resolver")]
    public void UseSecretStore_WhenAddSecretHasForeignResolver_ShouldThrowAndChangeNothing()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");
        var foreign = new ForeignAddSecretResolver();
        builder.Providers.CommandInputs.Add(foreign);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.UseSecretStore(store));

        // Assert
        exception.Message.ShouldContain(nameof(ForeignAddSecretResolver), Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
        builder.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeSameAs(foreign);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: Leaves resolvers for other command kinds in place")]
    public void UseSecretStore_WithResolverForOtherKind_ShouldAddAddSecretResolver()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");
        var other = new ForeignAddSecretResolver("web.add-route");
        builder.Providers.CommandInputs.Add(other);

        // Act
        builder.UseSecretStore(store);

        // Assert
        builder.Providers.CommandInputs.Count.ShouldBe(2);
        builder.Providers.CommandInputs[0].ShouldBeSameAs(other);
        builder.Providers.CommandInputs[1].ShouldBeOfType<SecretStoreAddSecretInputResolver>();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore: A store named like a built-in source is refused before anything is written")]
    public void UseSecretStore_WithReservedSourceName_ShouldThrowArgumentException()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(new TestResource("parameter"));

        // Act
        Should.Throw<ArgumentException>(() => builder.UseSecretStore(store));

        // Assert
        builder.Providers.Sources.ShouldBeEmpty();
        builder.Providers.CommandInputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsCertificateAuthority: Binds the store as the application's certificate authority")]
    public void AsCertificateAuthority_WhenUnbound_ShouldBindStore()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        SecretStoreProviderBuilder handle = builder.UseSecretStore(AddStore(builder, "secrets"));

        // Act
        SecretStoreProviderBuilder returned = handle.AsCertificateAuthority();

        // Assert
        returned.ShouldBeSameAs(handle);
        ResourceProviderBinding<IResourceCertificateAuthority> binding = builder.Providers.CertificateAuthority.ShouldNotBeNull();
        binding.Resource.ShouldBe((ResourceName)"secrets");
        binding.Provider.ShouldBeOfType<SecretStoreCertificateAuthority>();
        builder.Providers.TrustStore.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsCertificateAuthority: Binding the same store again changes nothing")]
    public void AsCertificateAuthority_CalledTwice_ShouldKeepBinding()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");
        builder.UseSecretStore(store).AsCertificateAuthority();
        ResourceProviderBinding<IResourceCertificateAuthority>? first = builder.Providers.CertificateAuthority;

        // Act
        builder.UseSecretStore(store).AsCertificateAuthority();

        // Assert
        builder.Providers.CertificateAuthority.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsCertificateAuthority: Refuses a second store as authority")]
    public void AsCertificateAuthority_WhenAnotherStoreIsAuthority_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        builder.UseSecretStore(AddStore(builder, "secrets")).AsCertificateAuthority();
        SecretStoreProviderBuilder second = builder.UseSecretStore(AddStore(builder, "vault"));
        ResourceProviderBinding<IResourceCertificateAuthority>? first = builder.Providers.CertificateAuthority;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => second.AsCertificateAuthority());

        // Assert
        exception.Message.ShouldContain("bound to resource 'secrets'", Case.Sensitive);
        exception.Message.ShouldContain("'vault'", Case.Sensitive);
        builder.Providers.CertificateAuthority.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsCertificateAuthority: Refuses to replace another authority bound to the same store")]
    public void AsCertificateAuthority_WhenForeignAuthorityBound_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        SecretStoreProviderBuilder handle = builder.UseSecretStore(AddStore(builder, "secrets"));
        var foreign = new ResourceProviderBinding<IResourceCertificateAuthority>(
            (ResourceName)"secrets",
            new ForeignCertificateAuthority());
        builder.Providers.CertificateAuthority = foreign;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => handle.AsCertificateAuthority());

        // Assert
        exception.Message.ShouldContain(nameof(ForeignCertificateAuthority), Case.Sensitive);
        builder.Providers.CertificateAuthority.ShouldBeSameAs(foreign);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsTrustStore: Binds the store as the application's trusted-issuer store")]
    public void AsTrustStore_WhenUnbound_ShouldBindStore()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        SecretStoreProviderBuilder handle = builder.UseSecretStore(AddStore(builder, "secrets"));

        // Act
        SecretStoreProviderBuilder returned = handle.AsTrustStore();

        // Assert
        returned.ShouldBeSameAs(handle);
        ResourceProviderBinding<ITrustedIssuerStore> binding = builder.Providers.TrustStore.ShouldNotBeNull();
        binding.Resource.ShouldBe((ResourceName)"secrets");
        binding.Provider.ShouldBeOfType<SecretStoreTrustedIssuerStore>();
        builder.Providers.CertificateAuthority.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsTrustStore: Binding the same store again changes nothing")]
    public void AsTrustStore_CalledTwice_ShouldKeepBinding()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        SecretStoreProviderBuilder handle = builder.UseSecretStore(AddStore(builder, "secrets")).AsTrustStore();
        ResourceProviderBinding<ITrustedIssuerStore>? first = builder.Providers.TrustStore;

        // Act
        handle.AsTrustStore();

        // Assert
        builder.Providers.TrustStore.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsTrustStore: Refuses a second store as trust store")]
    public void AsTrustStore_WhenAnotherStoreIsTrustStore_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        builder.UseSecretStore(AddStore(builder, "secrets")).AsTrustStore();
        SecretStoreProviderBuilder second = builder.UseSecretStore(AddStore(builder, "vault"));
        ResourceProviderBinding<ITrustedIssuerStore>? first = builder.Providers.TrustStore;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => second.AsTrustStore());

        // Assert
        exception.Message.ShouldContain("bound to resource 'secrets'", Case.Sensitive);
        builder.Providers.TrustStore.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AsTrustStore: Refuses to replace a trust store outside the model")]
    public void AsTrustStore_WhenForeignStoreBoundToNoResource_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        SecretStoreProviderBuilder handle = builder.UseSecretStore(AddStore(builder, "secrets"));
        var foreign = new ResourceProviderBinding<ITrustedIssuerStore>(null, new ForeignTrustedIssuerStore());
        builder.Providers.TrustStore = foreign;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => handle.AsTrustStore());

        // Assert
        exception.Message.ShouldContain("bound to no resource", Case.Sensitive);
        builder.Providers.TrustStore.ShouldBeSameAs(foreign);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SecretStoreProviderBuilder: Roles chain onto one store")]
    public void AsCertificateAuthority_ChainedWithAsTrustStore_ShouldBindBothRoles()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor store = AddStore(builder, "secrets");

        // Act
        builder.UseSecretStore(store)
            .AsCertificateAuthority()
            .AsTrustStore();

        // Assert
        builder.Providers.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
        builder.Providers.CertificateAuthority.ShouldNotBeNull().Resource.ShouldBe((ResourceName)"secrets");
        builder.Providers.TrustStore.ShouldNotBeNull().Resource.ShouldBe((ResourceName)"secrets");
        builder.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeOfType<SecretStoreAddSecretInputResolver>();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Build: UseSecretStore satisfies the add-secret input-resolver requirement the SecretStore manifest declares")]
    public void Build_AddSecretWithUseSecretStore_ShouldSucceed()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuildableBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(AddSecretStoreManifest());
        DeclareAddSecret(builder, store);
        builder.UseSecretStore(store);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeOfType<SecretStoreAddSecretInputResolver>();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Build: an add-secret declared without UseSecretStore fails and names the package and verb")]
    public void Build_AddSecretWithoutUseSecretStore_ShouldNamePackageAndVerb()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuildableBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(AddSecretStoreManifest());
        DeclareAddSecret(builder, store);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("command 'secretstore.add-secret'", Case.Sensitive);
        exception.Message.ShouldContain("Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration", Case.Sensitive);
        exception.Message.ShouldContain("builder.UseSecretStore(...)", Case.Sensitive);
    }

    private static IApplicationBuilder CreateBuildableBuilder() =>
        Application.CreateBuilder(ApplicationName.Parse("appa"), ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(new StoreGateway(new StoreController("secrets", new Dictionary<string, int>())));

    // The shape Sdk.SecretStore produces: add-secret carries a source the gateway must resolve.
    private static ResourceManifest AddSecretStoreManifest() => new()
    {
        Name = "secrets",
        Kind = "SecretStore",
        Application = "appa",
        ApplicationModel = "Example.Store.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Example.Store",
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
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
            Replicas = 1,
            MaxReplicas = 1,
        },
        Commands = [new ResourceManifestCommand("secretstore.add-secret") { RequiresInputResolver = true }],
    };

    private static void DeclareAddSecret(IApplicationBuilder builder, IApplicationResourceDescriptor store)
    {
        using JsonDocument payload = JsonDocument.Parse("{\"path\":\"tls\",\"source\":\"parameter:tls\"}");
        builder.AddCommand(ResourceCommands.Create(
            "secretstore.add-secret", "tls", store.Resource, ApplicationName.Parse("appa"), payload.RootElement,
            CommandPayloadJsonContext.Default.JsonElement));
    }

    private static IApplicationResourceDescriptor AddStore(IApplicationBuilder builder, string name, string kind = "SecretStore") =>
        builder.AddResource(new ResourceManifest
        {
            Name = name,
            Kind = kind,
            Application = "appa",
        });

    private sealed class ForeignSourceProvider : IResourceSourceProvider
    {
        public string? ResourceKind => null;
    }

    private sealed class ForeignAddSecretResolver : IResourceCommandInputResolver
    {
        internal ForeignAddSecretResolver(string commandKind = "secretstore.add-secret")
        {
            CommandKind = commandKind;
        }

        public string CommandKind { get; }

        public ValueTask<ReadOnlyMemory<byte>> ResolveAsync(
            ResourceCommandInput declared,
            IResourceSourceResolver sources,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(declared.Payload);
    }

    private sealed class ForeignCertificateAuthority : IResourceCertificateAuthority
    {
        public string? ResourceKind => null;

        public ValueTask<ResourceCertificate> IssueAsync(
            ResourceCertificateRequest request,
            ResourceProviderConnection? authority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ForeignTrustedIssuerStore : ITrustedIssuerStore
    {
        public string? ResourceKind => null;

        public ValueTask<IReadOnlyList<TrustedIssuer>?> ReadAsync(
            ResourceProviderConnection? store,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TrustedIssuer>?>(null);

        public ValueTask AddAsync(
            ResourceProviderConnection? store,
            string owner,
            TrustedIssuer issuer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
