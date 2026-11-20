using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Proxy;

// Both proxies register through the application's service registrations, as builder.Services.AddReverseProxy(...)
// and builder.Services.AddForwardProxy(...), the way every Web feature registers. Each verb configures everything;
// UseReverseProxy() and UseForwardProxy() only place the middleware (owner decision, 2026-10-28). What each registers
// is a singleton IWebApplicationSetting the context lists on Settings, so an operator surface can replace the routes
// or the rules while the application runs. The setting owns the proxy's connection pool, and the container disposes it
// with the application. The shared catalog names the seam without a reference, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ReverseProxyBuilder),
    factoryMethodName: nameof(ReverseProxyBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddReverseProxy",
    Contract = typeof(IWebApplicationSetting))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ReverseProxyBuilder),
    factoryMethodName: nameof(ReverseProxyBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddReverseProxy",
    Contract = typeof(IWebApplicationSetting))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ForwardProxyBuilder),
    factoryMethodName: nameof(ForwardProxyBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddForwardProxy",
    Contract = typeof(IWebApplicationSetting))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ForwardProxyBuilder),
    factoryMethodName: nameof(ForwardProxyBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddForwardProxy",
    Contract = typeof(IWebApplicationSetting))]
