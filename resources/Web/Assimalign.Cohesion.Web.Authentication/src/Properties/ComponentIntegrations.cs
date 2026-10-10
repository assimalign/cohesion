using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;

// Owner decision 34 (#1380): authentication registers through the application's service registrations
// as builder.Services.AddAuthentication(authentication => authentication.AddCookie(...)). Naming the
// public AuthenticationBuilder.Build instance method selects the generator's builder template, whose
// configure callback receives the builder the scheme packages graft onto. The seam is named as a
// string, so this project takes no reference to DI. Decision 35: every IHttpFeature registration is a
// singleton, which Web.Hosting enforces at Build.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(AuthenticationBuilder),
    factoryMethodName: nameof(AuthenticationBuilder.Build),
    Verb = "AddAuthentication",
    Contract = typeof(IHttpFeature))]
