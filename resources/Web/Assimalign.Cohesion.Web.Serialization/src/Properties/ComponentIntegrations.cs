using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization;

// Owner decision 34 (#1380): the content-serialization registry registers through the application's
// service registrations. The seam is named as a string, so this project takes no reference to DI.
// Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting enforces at Build.

// builder.Services.AddContentSerialization(serialization => serialization.AddJson(...)): naming the
// public ContentSerializationBuilder.Build instance method selects the generator's builder template,
// which takes the configure callback format packages graft onto.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(ContentSerializationBuilder),
    factoryMethodName: nameof(ContentSerializationBuilder.Build),
    Verb = "AddContentSerialization",
    Contract = typeof(IHttpFeature))]

// builder.Services.AddJsonSerialization(resolver, configure): the JSON shorthand, a static factory
// because the resolver is an argument the builder template cannot carry.
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(SerializationComponents),
    factoryMethodName: nameof(SerializationComponents.CreateJsonFeature),
    Verb = "AddJsonSerialization",
    Contract = typeof(IHttpFeature))]
