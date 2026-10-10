using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Antiforgery;

// Owner decision 34 (#1380): antiforgery registers through the application's service registrations as
// builder.Services.AddAntiforgery(...); both CreateFeature overloads are projected. The seam is named as
// a string, so this project takes no reference to DI. Decision 35: every IHttpFeature registration is a
// singleton, which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(AntiforgeryComponents),
    factoryMethodName: nameof(AntiforgeryComponents.CreateFeature),
    Verb = "AddAntiforgery",
    Contract = typeof(IHttpFeature))]
