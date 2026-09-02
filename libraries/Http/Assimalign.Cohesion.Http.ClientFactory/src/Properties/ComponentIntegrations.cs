using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;

// Naming the already-public HttpClientFactoryBuilder.Build instance method selects the generator's
// builder template, contributing AddHttpClientFactory in consuming compilations with zero added
// public surface. The seam is named as a string, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(HttpClientFactoryBuilder),
    factoryMethodName: nameof(HttpClientFactoryBuilder.Build),
    Verb = "AddHttpClientFactory",
    Contract = typeof(IHttpClientFactory))]
