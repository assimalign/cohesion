using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Sessions;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddSessions(sessions => ...) builds the
// configured middleware and registers it, and UseSessions() places the registration it finds in
// IWebApplicationContext.Middleware. Naming the builder's public Build instance method selects the generator's
// builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(SessionsBuilder),
    factoryMethodName: nameof(SessionsBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddSessions",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(SessionsBuilder),
    factoryMethodName: nameof(SessionsBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddSessions",
    Contract = typeof(IWebApplicationMiddleware))]
