using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.CookiePolicy;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddCookiePolicy(policy => ...) builds
// the middleware over the validated policy and registers it, and UseCookiePolicy() places the registration it finds
// in IWebApplicationContext.Middleware. Naming the public CookiePolicyBuilder.Build instance method selects the
// generator's builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(CookiePolicyBuilder),
    factoryMethodName: nameof(CookiePolicyBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddCookiePolicy",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(CookiePolicyBuilder),
    factoryMethodName: nameof(CookiePolicyBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddCookiePolicy",
    Contract = typeof(IWebApplicationMiddleware))]
