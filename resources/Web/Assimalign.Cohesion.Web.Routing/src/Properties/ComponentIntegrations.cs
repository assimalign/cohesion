using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

// Owner decision 34 (#1380): routing registers through the application's service registrations as
// builder.Services.AddRouting(). The seam is named as a string, so this project takes no reference to
// DI. Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(RoutingComponents),
    factoryMethodName: nameof(RoutingComponents.CreateFeature),
    Verb = "AddRouting",
    Contract = typeof(IHttpFeature))]
