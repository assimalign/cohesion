using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Rewrite;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddRewrite(rewrite => ...) builds the
// rewrite middleware over the rules it adds and registers it, and UseRewrite() places the registration it finds in
// IWebApplicationContext.Middleware. Naming the builder's public Build instance method selects the generator's
// builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RewriteBuilder),
    factoryMethodName: nameof(RewriteBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddRewrite",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RewriteBuilder),
    factoryMethodName: nameof(RewriteBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddRewrite",
    Contract = typeof(IWebApplicationMiddleware))]
