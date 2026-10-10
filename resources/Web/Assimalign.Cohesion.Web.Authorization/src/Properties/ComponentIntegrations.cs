using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authorization;

// Owner decision 34 (#1380): authorization registers through the application's service registrations
// as builder.Services.AddAuthorization(...). The seam is named as a string, so this project takes no
// reference to DI. Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting
// enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(AuthorizationComponents),
    factoryMethodName: nameof(AuthorizationComponents.CreateFeature),
    Verb = "AddAuthorization",
    Contract = typeof(IHttpFeature))]
