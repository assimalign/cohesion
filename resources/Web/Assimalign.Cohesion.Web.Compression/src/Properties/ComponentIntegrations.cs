using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Compression;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddResponseCompression(compression => ...)
// and builder.Services.AddRequestDecompression(decompression => ...) build the configured middleware and register it,
// and UseResponseCompression() / UseRequestDecompression() place the registration they find in
// IWebApplicationContext.Middleware. Naming each builder's public Build instance method selects the generator's
// builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ResponseCompressionBuilder),
    factoryMethodName: nameof(ResponseCompressionBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddResponseCompression",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ResponseCompressionBuilder),
    factoryMethodName: nameof(ResponseCompressionBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddResponseCompression",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RequestDecompressionBuilder),
    factoryMethodName: nameof(RequestDecompressionBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddRequestDecompression",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(RequestDecompressionBuilder),
    factoryMethodName: nameof(RequestDecompressionBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddRequestDecompression",
    Contract = typeof(IWebApplicationMiddleware))]
