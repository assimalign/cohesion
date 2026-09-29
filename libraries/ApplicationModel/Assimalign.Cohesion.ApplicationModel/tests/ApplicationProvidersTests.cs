using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ApplicationProvidersTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: Empty is frozen and carries no registrations")]
    public void Empty_Always_ShouldBeFrozenAndEmpty()
    {
        // Arrange
        ApplicationProviders empty = ApplicationProviders.Empty;

        // Act & Assert
        empty.IsFrozen.ShouldBeTrue();
        empty.Sources.ShouldBeEmpty();
        empty.CommandInputs.ShouldBeEmpty();
        empty.Callers.ShouldBeEmpty();
        empty.CertificateAuthority.ShouldBeNull();
        empty.TrustStore.ShouldBeNull();
        empty.Telemetry.ShouldBeNull();
        empty.CredentialIssuer.ShouldBeNull();
        empty.Sources.IsReadOnly.ShouldBeTrue();
        empty.CommandInputs.IsReadOnly.ShouldBeTrue();
        empty.Callers.IsReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => empty.CredentialIssuer = new FakeCredentialIssuer());
        Should.Throw<NotSupportedException>(() => empty.Sources.Add("vault", new EmptySourceProvider()));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: a frozen instance rejects every setter and collection mutation")]
    public void ToFrozen_Mutation_ShouldThrow()
    {
        // Arrange
        ApplicationProviders frozen = new ApplicationProviders().ToFrozen();
        var resource = (ResourceName)"secrets";

        // Act & Assert
        Should.Throw<InvalidOperationException>(() =>
            frozen.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>(resource, new FakeCertificateAuthority()));
        Should.Throw<InvalidOperationException>(() =>
            frozen.TrustStore = new ResourceProviderBinding<ITrustedIssuerStore>(resource, new FakeTrustedIssuerStore()));
        Should.Throw<InvalidOperationException>(() =>
            frozen.Telemetry = ResourceTelemetrySink.External(new Uri("https://otel.example.test:4318")));
        Should.Throw<InvalidOperationException>(() => frozen.CredentialIssuer = null);
        Should.Throw<NotSupportedException>(() => frozen.Sources["vault"] = new EmptySourceProvider());
        Should.Throw<NotSupportedException>(() => frozen.Sources.Remove("vault"));
        Should.Throw<NotSupportedException>(() => frozen.CommandInputs.Add(new FakeCommandInputResolver()));
        Should.Throw<NotSupportedException>(() => frozen.CommandInputs.Clear());
        Should.Throw<NotSupportedException>(() => frozen.Callers.Add(new FakeCallerAuthenticator()));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: ToFrozen copies the registrations and leaves the source mutable")]
    public void ToFrozen_MutableSource_ShouldSnapshotAndStayMutable()
    {
        // Arrange
        var providers = new ApplicationProviders();
        var source = new SecretOnlySourceProvider();
        var authority = new ResourceProviderBinding<IResourceCertificateAuthority>("secrets", new FakeCertificateAuthority());
        var trustStore = new ResourceProviderBinding<ITrustedIssuerStore>(null, new FakeTrustedIssuerStore());
        var input = new FakeCommandInputResolver();
        var caller = new FakeCallerAuthenticator();
        var issuer = new FakeCredentialIssuer();
        ResourceTelemetrySink telemetry = ResourceTelemetrySink.External(new Uri("https://otel.example.test:4318"), "otlp-headers");
        providers.Sources["vault"] = source;
        providers.CertificateAuthority = authority;
        providers.TrustStore = trustStore;
        providers.CommandInputs.Add(input);
        providers.Callers.Add(caller);
        providers.CredentialIssuer = issuer;
        providers.Telemetry = telemetry;

        // Act
        ApplicationProviders frozen = providers.ToFrozen();
        providers.Sources["other"] = new EmptySourceProvider();
        providers.CommandInputs.Clear();
        providers.CredentialIssuer = null;

        // Assert
        providers.IsFrozen.ShouldBeFalse();
        frozen.IsFrozen.ShouldBeTrue();
        frozen.ToFrozen().ShouldBeSameAs(frozen);
        frozen.Sources.Count.ShouldBe(1);
        frozen.Sources["vault"].ShouldBeSameAs(source);
        frozen.CertificateAuthority.ShouldBeSameAs(authority);
        frozen.TrustStore.ShouldBeSameAs(trustStore);
        frozen.CommandInputs.ShouldHaveSingleItem().ShouldBeSameAs(input);
        frozen.Callers.ShouldHaveSingleItem().ShouldBeSameAs(caller);
        frozen.CredentialIssuer.ShouldBeSameAs(issuer);
        frozen.Telemetry.ShouldBeSameAs(telemetry);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: Build copies builder registrations into the model as a frozen snapshot")]
    public void Build_WithRegistrations_ShouldCarryFrozenSnapshot()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create("api"));
        var source = new SecretOnlySourceProvider();
        var input = new FakeCommandInputResolver();
        builder.Providers.Sources["vault"] = source;
        builder.Providers.CommandInputs.Add(input);

        // Act
        IApplicationModel model = builder.Build().Model;
        builder.Providers.Sources["late"] = new EmptySourceProvider();
        builder.Providers.CommandInputs.Clear();

        // Assert
        builder.Providers.IsFrozen.ShouldBeFalse();
        model.Providers.ShouldNotBeSameAs(builder.Providers);
        model.Providers.IsFrozen.ShouldBeTrue();
        model.Providers.Sources.Keys.ShouldBe(["vault"]);
        model.Providers.Sources["vault"].ShouldBeSameAs(source);
        model.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeSameAs(input);
        Should.Throw<InvalidOperationException>(() => model.Providers.CredentialIssuer = new FakeCredentialIssuer());
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: a model built without registrations carries a frozen empty snapshot")]
    public void Build_WithoutRegistrations_ShouldCarryFrozenEmptyProviders()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create("api"));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Providers.IsFrozen.ShouldBeTrue();
        model.Providers.Sources.ShouldBeEmpty();
        model.Providers.CommandInputs.ShouldBeEmpty();
        model.Providers.Callers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: the model factory snapshot sees registrations made so far")]
    public void AddResource_FactorySnapshot_ShouldSeeFrozenRegistrations()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        var source = new SecretOnlySourceProvider();
        builder.Providers.Sources["vault"] = source;
        ApplicationProviders? observed = null;

        // Act
        builder.AddResource(snapshot =>
        {
            observed = snapshot.Providers;
            return new FakeResource("api");
        });

        // Assert
        observed.ShouldNotBeNull();
        observed.IsFrozen.ShouldBeTrue();
        observed.Sources["vault"].ShouldBeSameAs(source);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: a model implementation without providers defaults to Empty")]
    public void Providers_DefaultInterfaceMember_ShouldReturnEmpty()
    {
        // Arrange
        IApplicationModel model = new MinimalApplicationModel();

        // Act
        ApplicationProviders providers = model.Providers;

        // Assert
        providers.ShouldBeSameAs(ApplicationProviders.Empty);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Providers: Sources rejects keys a mount source can never name")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("vault:key")]
    [InlineData("parameter")]
    [InlineData("literal")]
    public void Sources_InvalidKey_ShouldThrowArgumentException(string key)
    {
        // Arrange
        var providers = new ApplicationProviders();

        // Act & Assert
        Should.Throw<ArgumentException>(() => providers.Sources.Add(key, new EmptySourceProvider()));
        Should.Throw<ArgumentException>(() => providers.Sources[key] = new EmptySourceProvider());
        providers.Sources.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: collections reject null providers")]
    public void Collections_NullProvider_ShouldThrowArgumentNullException()
    {
        // Arrange
        var providers = new ApplicationProviders();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => providers.Sources["vault"] = null!);
        Should.Throw<ArgumentNullException>(() => providers.CommandInputs.Add(null!));
        Should.Throw<ArgumentNullException>(() => providers.Callers.Add(null!));
        providers.Sources.ShouldBeEmpty();
        providers.CommandInputs.ShouldBeEmpty();
        providers.Callers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Providers: Sources compares keys ordinally")]
    public void Sources_KeyComparison_ShouldBeOrdinal()
    {
        // Arrange
        var providers = new ApplicationProviders();
        providers.Sources["vault"] = new EmptySourceProvider();

        // Act & Assert
        providers.Sources.ContainsKey("vault").ShouldBeTrue();
        providers.Sources.ContainsKey("Vault").ShouldBeFalse();
    }

    private static IApplicationBuilder CreateBuilder() => Application
        .CreateBuilder(ApplicationName.Parse("appa"), [])
        .UseGateway(new FakeGateway());
}
