using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

// Owner decision 34 (#1380): routing registers through the application's service registrations as
// builder.Services.AddRouting(). Naming the public RoutingBuilder.Build instance method selects the
// generator's builder template, whose configure callback is optional (decision 39, #1395), so the verb
// is called bare. The shared catalog names the seam without a reference, so this project takes no reference to DI. Decision 35:
// every IHttpFeature registration is a singleton, which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RoutingBuilder),
    factoryMethodName: nameof(RoutingBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddRouting",
    Contract = typeof(IHttpFeature))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RoutingBuilder),
    factoryMethodName: nameof(RoutingBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddRouting",
    Contract = typeof(IHttpFeature))]
