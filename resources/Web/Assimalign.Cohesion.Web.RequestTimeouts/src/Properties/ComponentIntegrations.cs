using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.RequestTimeouts;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddRequestTimeouts(timeouts => ...)
// builds the configured middleware and registers it, and UseRequestTimeouts() places the registration it finds in
// IWebApplicationContext.Middleware. Naming the builder's public Build instance method selects the generator's
// builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RequestTimeoutsBuilder),
    factoryMethodName: nameof(RequestTimeoutsBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddRequestTimeouts",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RequestTimeoutsBuilder),
    factoryMethodName: nameof(RequestTimeoutsBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddRequestTimeouts",
    Contract = typeof(IWebApplicationMiddleware))]
