using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HttpsPolicy;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddHsts(hsts => ...) and
// builder.Services.AddHttpsRedirection(redirection => ...) build the configured middleware and register it,
// and UseHsts() / UseHttpsRedirection() place the registration they find in IWebApplicationContext.Middleware.
// Naming each builder's public Build instance method selects the generator's builder templates, so this project
// takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HstsBuilder),
    factoryMethodName: nameof(HstsBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddHsts",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HstsBuilder),
    factoryMethodName: nameof(HstsBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddHsts",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HttpsRedirectionBuilder),
    factoryMethodName: nameof(HttpsRedirectionBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddHttpsRedirection",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HttpsRedirectionBuilder),
    factoryMethodName: nameof(HttpsRedirectionBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddHttpsRedirection",
    Contract = typeof(IWebApplicationMiddleware))]
