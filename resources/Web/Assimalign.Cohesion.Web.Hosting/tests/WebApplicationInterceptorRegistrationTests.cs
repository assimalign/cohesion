using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class WebApplicationInterceptorRegistrationTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Interceptor registration: Defaults resolve as the registered singleton instances in order")]
    public async Task Build_DefaultInterceptors_ShouldResolveRegisteredSingletonsInOrder()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ServiceDescriptor[] descriptors = builder.Services.Container
            .Where(descriptor => descriptor.ServiceType == typeof(IHttpExchangeInterceptor)).ToArray();

        // Act
        await using WebApplication application = builder.Build();
        IHttpExchangeInterceptor[] interceptors = application.Context.ServiceProvider
            .GetRequiredService<IEnumerable<IHttpExchangeInterceptor>>().ToArray();
        IHttpExchangeInterceptor[] resolvedAgain = application.Context.ServiceProvider
            .GetRequiredService<IEnumerable<IHttpExchangeInterceptor>>().ToArray();

        // Assert
        interceptors.Select(interceptor => interceptor.GetType()).ShouldBe(new[]
        {
            typeof(HttpRequestLimitsInterceptor),
            typeof(HttpProtocolUpgradeInterceptor),
            typeof(HttpExtendedConnectInterceptor),
        });
        descriptors.Length.ShouldBe(3);
        for (int index = 0; index < descriptors.Length; index++)
        {
            descriptors[index].Lifetime.ShouldBe(ServiceLifetime.Singleton);
            interceptors[index].ShouldBeSameAs(descriptors[index].ImplementationInstance);
            resolvedAgain[index].ShouldBeSameAs(interceptors[index]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Interceptor registration: A non-singleton fails Build and names its registration")]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task Build_WithNonSingletonInterceptor_ShouldThrowNamingRegistration(ServiceLifetime lifetime)
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        int index = builder.Services.Container.Count;
        if (lifetime == ServiceLifetime.Scoped)
        {
            builder.Services.AddScoped<IHttpExchangeInterceptor>(_ => new RequestHookInterceptor());
        }
        else
        {
            builder.Services.AddTransient<IHttpExchangeInterceptor>(_ => new RequestHookInterceptor());
        }

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert — validation precedes build-owned registrations and MakeReadOnly, so callers can fix it.
        exception.Message.ShouldContain($"builder.Services[{index}]", Case.Sensitive);
        exception.Message.ShouldContain(nameof(IHttpExchangeInterceptor), Case.Sensitive);
        exception.Message.ShouldContain($"is {lifetime}", Case.Sensitive);
        exception.Message.ShouldContain("must be a singleton", Case.Sensitive);
        builder.Services.Container.Count.ShouldBe(index + 1);
        builder.Services.Container.Unregister(builder.Services.Container[index]).ShouldBeTrue();
        await using WebApplication application = builder.Build();
        application.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Interceptor registration: A narrower service type fails Build and names its registration")]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_WithNarrowerInterceptorType_ShouldThrowNamingRegistration(bool useBaseType)
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        int index = builder.Services.Container.Count;
        Type serviceType;
        if (useBaseType)
        {
            serviceType = typeof(HttpExchangeInterceptor);
            builder.Services.AddSingleton<HttpExchangeInterceptor>(new RequestHookInterceptor());
        }
        else
        {
            serviceType = typeof(RequestHookInterceptor);
            builder.Services.AddSingleton<RequestHookInterceptor>(new RequestHookInterceptor());
        }

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain($"builder.Services[{index}]", Case.Sensitive);
        exception.Message.ShouldContain(serviceType.FullName!, Case.Sensitive);
        exception.Message.ShouldContain(typeof(RequestHookInterceptor).FullName!, Case.Sensitive);
        exception.Message.ShouldContain("is not IHttpExchangeInterceptor", Case.Sensitive);
    }
}
