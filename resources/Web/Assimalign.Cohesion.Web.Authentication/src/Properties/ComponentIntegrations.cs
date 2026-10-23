using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;

// Owner decision 34 (#1380): authentication registers through the application's service registrations
// as builder.Services.AddAuthentication(authentication => authentication.AddCookie(...)). Naming the
// public AuthenticationBuilder.Build instance method selects the generator's builder template, whose
// configure callback receives the builder the scheme packages graft onto. The shared catalog names the seam without a reference, so this project takes no reference to DI. Decision 35: every IHttpFeature registration is a
// singleton, which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(AuthenticationBuilder),
    factoryMethodName: nameof(AuthenticationBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddAuthentication",
    Contract = typeof(IHttpFeature))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(AuthenticationBuilder),
    factoryMethodName: nameof(AuthenticationBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddAuthentication",
    Contract = typeof(IHttpFeature))]
