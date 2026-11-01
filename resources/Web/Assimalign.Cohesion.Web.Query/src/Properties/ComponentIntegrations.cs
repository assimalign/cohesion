using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Query;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddQueryValidation(validation => ...) and
// builder.Services.AddQueryConditionals(conditionals => conditionals.UseResourceValidators(...)) build the configured
// middleware and register it, and UseQueryValidation() / UseQueryConditionals() place the registration they find in
// IWebApplicationContext.Middleware. Naming each builder's public Build instance method selects the generator's builder
// templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(WebQueryValidationBuilder),
    factoryMethodName: nameof(WebQueryValidationBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddQueryValidation",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(WebQueryValidationBuilder),
    factoryMethodName: nameof(WebQueryValidationBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddQueryValidation",
    Contract = typeof(IWebApplicationMiddleware))]

// Deviates from the repo web-area rule "a required value argument keeps the static factory" (owner decision 39) per
// the 2026-10-28 "Add configures, Use places" migration brief: the required resource-validators provider is the builder
// method WebQueryConditionalsBuilder.UseResourceValidators, and Build throws InvalidOperationException when it is
// missing, so the error surfaces at registration rather than at compile time (the AddForwardedHeaders no-default shape).
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(WebQueryConditionalsBuilder),
    factoryMethodName: nameof(WebQueryConditionalsBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddQueryConditionals",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(WebQueryConditionalsBuilder),
    factoryMethodName: nameof(WebQueryConditionalsBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddQueryConditionals",
    Contract = typeof(IWebApplicationMiddleware))]
