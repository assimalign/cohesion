using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.OpenApi;

// Owner decision 34 (#1380): the OpenAPI document options register through the application's service
// registrations as builder.Services.AddOpenApi(openApi => ...). Naming the public OpenApiBuilder.Build
// instance method selects the generator's builder template, whose configure callback is optional
// (decision 39, #1395), so AddOpenApi() registers the default document. The seam is named as a string,
// so this project takes no reference to DI. Decision 35: every IHttpFeature registration is a singleton,
// which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(OpenApiBuilder),
    factoryMethodName: nameof(OpenApiBuilder.Build),
    Verb = "AddOpenApi",
    Contract = typeof(IHttpFeature))]
