using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Antiforgery;

// Owner decision 34 (#1380): antiforgery registers through the application's service registrations as
// builder.Services.AddAntiforgery(antiforgery => ...). Naming the public AntiforgeryBuilder.Build instance
// method selects the generator's builder template, whose configure callback is optional (decision 39,
// #1395): AddAntiforgery() is the development default, and the data-protection provider the former
// overload took is antiforgery.UseDataProtection(provider). The shared catalog names the seam without a reference, so this project
// takes no reference to DI. Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting
// enforces at Build.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(AntiforgeryBuilder),
    factoryMethodName: nameof(AntiforgeryBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddAntiforgery",
    Contract = typeof(IHttpFeature))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(AntiforgeryBuilder),
    factoryMethodName: nameof(AntiforgeryBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddAntiforgery",
    Contract = typeof(IHttpFeature))]
