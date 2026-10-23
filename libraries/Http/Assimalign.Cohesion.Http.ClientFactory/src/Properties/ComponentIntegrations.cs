using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;

// Naming the already-public HttpClientFactoryBuilder.Build instance method selects the generator's
// builder template, contributing AddHttpClientFactory in consuming compilations with zero added
// public surface. The shared catalog names the seam without a reference, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HttpClientFactoryBuilder),
    factoryMethodName: nameof(HttpClientFactoryBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddHttpClientFactory",
    Contract = typeof(IHttpClientFactory))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HttpClientFactoryBuilder),
    factoryMethodName: nameof(HttpClientFactoryBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddHttpClientFactory",
    Contract = typeof(IHttpClientFactory))]
