using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authorization;

// Owner decision 34 (#1380): authorization registers through the application's service registrations
// as builder.Services.AddAuthorization(authorization => ...). Naming the public AuthorizationBuilder.Build
// instance method selects the generator's builder template, whose configure callback is optional
// (decision 39, #1395), so AddAuthorization() registers the defaults. The shared catalog names the seam without a reference, so
// this project takes no reference to DI. Decision 35: every IHttpFeature registration is a singleton,
// which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(AuthorizationBuilder),
    factoryMethodName: nameof(AuthorizationBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddAuthorization",
    Contract = typeof(IHttpFeature))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(AuthorizationBuilder),
    factoryMethodName: nameof(AuthorizationBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddAuthorization",
    Contract = typeof(IHttpFeature))]
