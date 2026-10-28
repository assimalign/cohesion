using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.StaticFiles;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddStaticFiles(files => ...) builds
// the middleware over the configured file system and options and registers it, and UseStaticFiles() places the
// registration it finds in IWebApplicationContext.Middleware, mounting the application's web root when the
// registration named no file system. Naming the public StaticFilesBuilder.Build instance method selects the
// generator's builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(StaticFilesBuilder),
    factoryMethodName: nameof(StaticFilesBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddStaticFiles",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(StaticFilesBuilder),
    factoryMethodName: nameof(StaticFilesBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddStaticFiles",
    Contract = typeof(IWebApplicationMiddleware))]
