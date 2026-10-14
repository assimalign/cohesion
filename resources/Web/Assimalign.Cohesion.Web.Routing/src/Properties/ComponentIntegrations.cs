using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

// Owner decision 34 (#1380): routing registers through the application's service registrations as
// builder.Services.AddRouting(). Naming the public RoutingBuilder.Build instance method selects the
// generator's builder template, whose configure callback is optional (decision 39, #1395), so the verb
// is called bare. The seam is named as a string, so this project takes no reference to DI. Decision 35:
// every IHttpFeature registration is a singleton, which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(RoutingBuilder),
    factoryMethodName: nameof(RoutingBuilder.Build),
    Verb = "AddRouting",
    Contract = typeof(IHttpFeature))]
