using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.OpenApi;

// Owner decision 34 (#1380): the OpenAPI document options register through the application's service
// registrations as builder.Services.AddOpenApi(...). The seam is named as a string, so this project
// takes no reference to DI. Decision 35: every IHttpFeature registration is a singleton, which
// Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(OpenApiComponents),
    factoryMethodName: nameof(OpenApiComponents.CreateFeature),
    Verb = "AddOpenApi",
    Contract = typeof(IHttpFeature))]
