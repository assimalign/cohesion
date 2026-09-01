using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;

// Projects HttpClientFactoryComponents.CreateHttpClientFactory onto
// Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder as AddHttpClientFactory,
// in any compilation that references BOTH this assembly and the DI assembly. The seam is
// named as a string, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(HttpClientFactoryComponents),
    factoryMethodName: nameof(HttpClientFactoryComponents.CreateHttpClientFactory),
    Verb = "AddHttpClientFactory",
    Contract = typeof(IHttpClientFactory))]
