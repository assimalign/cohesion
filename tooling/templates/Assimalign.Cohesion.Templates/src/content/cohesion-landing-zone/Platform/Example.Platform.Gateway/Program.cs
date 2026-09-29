using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// The enabled resource projects provide their area-owned manifests and default control planes.
ISecretStoreResourceDescriptor secrets = builder.AddSecretStore(Manifests.PlatformSecretStore);
IConfigurationStoreResourceDescriptor configuration = builder.AddConfigurationStore(Manifests.PlatformConfigurationStore);
ILogSpaceResourceDescriptor logs = builder.AddLogSpace(Manifests.PlatformLogSpace);

// Nothing is registered by convention. The SecretStore resolves 'platform-secretstore:<key>' mounts
// and is the application's certificate authority and trusted-issuer store, the ConfigurationStore
// resolves 'platform-configurationstore:<namespace>' mounts, and LogSpace receives the telemetry of
// every resource. The two Orchestration packages referenced by the csproj supply these verbs.
builder.UseSecretStore(secrets).AsCertificateAuthority().AsTrustStore();
builder.UseConfigurationStore(configuration);
builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(logs);
builder.UseGateway(args);

await builder.Build().RunAsync();
