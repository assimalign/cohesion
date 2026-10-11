using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Antiforgery;

// Owner decision 34 (#1380): antiforgery registers through the application's service registrations as
// builder.Services.AddAntiforgery(antiforgery => ...). Naming the public AntiforgeryBuilder.Build instance
// method selects the generator's builder template, whose configure callback is optional (decision 39,
// #1395): AddAntiforgery() is the development default, and the data-protection provider the former
// overload took is antiforgery.UseDataProtection(provider). The seam is named as a string, so this project
// takes no reference to DI. Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting
// enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(AntiforgeryBuilder),
    factoryMethodName: nameof(AntiforgeryBuilder.Build),
    Verb = "AddAntiforgery",
    Contract = typeof(IHttpFeature))]
