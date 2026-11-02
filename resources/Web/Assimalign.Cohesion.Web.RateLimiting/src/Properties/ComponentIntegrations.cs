using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.RateLimiting;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddRateLimiting(rateLimiting => ...)
// builds the middleware over the configured global limiter, named policies, and rejection handling and registers it,
// and UseRateLimiting() places the registration it finds in IWebApplicationContext.Middleware. Naming the public
// RateLimitingBuilder.Build instance method selects the generator's builder templates, so this project takes no
// reference to DI. The registered middleware is a container-owned singleton, disposed with the application.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RateLimitingBuilder),
    factoryMethodName: nameof(RateLimitingBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddRateLimiting",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RateLimitingBuilder),
    factoryMethodName: nameof(RateLimitingBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddRateLimiting",
    Contract = typeof(IWebApplicationMiddleware))]
