// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.DependencyInjection.Specification.Fakes;
using Xunit;

namespace Assimalign.Cohesion.DependencyInjection.Specification;

using Utilities;

public abstract partial class DependencyInjectionSpecificationTests
{
    // for most DI providers, the structs default constructor shouldn't run when creating an instance of ClassWithOptionalArgsCtorWithStructs
    public virtual bool ExpectStructWithPublicDefaultConstructorInvoked => false;

    public delegate object CreateInstanceFunc(IServiceProvider provider, Type type, object[] args);

    private static object CreateInstanceDirectly(IServiceProvider provider, Type type, object[] args)
    {
        return ActivatorUtilities.CreateInstance(provider, type, args);
    }

    private static object CreateInstanceFromFactory(IServiceProvider provider, Type type, object[] args)
    {
        var factory = ActivatorUtilities.CreateFactory(type, args.Select(a => a.GetType()).ToArray());
        return factory(provider, args);
    }

    private static T CreateInstance<T>(CreateInstanceFunc func, IServiceProvider provider, params object[] args)
    {
        return (T)func(provider, typeof(T), args);
    }

    public static IEnumerable<object[]> CreateInstanceFuncs
    {
        get
        {
            yield return new[] { (CreateInstanceFunc)CreateInstanceDirectly };
            yield return new[] { (CreateInstanceFunc)CreateInstanceFromFactory };
        }
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorEnablesYouToCreateAnyTypeWithServicesEvenWhenNotInIocContainer(CreateInstanceFunc createFunc)
    {
        // Arrange
        var serviceCollection = new TestServiceCollection()
            .AddTransient<IFakeService, FakeService>();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        var anotherClass = CreateInstance<AnotherClass>(createFunc, serviceProvider);

        Assert.NotNull(anotherClass.FakeService);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorAcceptsAnyNumberOfAdditionalConstructorParametersToProvide(CreateInstanceFunc createFunc)
    {
        // Arrange
        var serviceCollection = new TestServiceCollection()
            .AddTransient<IFakeService, FakeService>();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        // Act
        var anotherClass = CreateInstance<AnotherClassAcceptingData>(createFunc, serviceProvider, "1", "2");

        // Assert
        Assert.NotNull(anotherClass.FakeService);
        Assert.Equal("1", anotherClass.One);
        Assert.Equal("2", anotherClass.Two);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorWorksWithStaticCtor(CreateInstanceFunc createFunc)
    {
        // Act
        var serviceCollection = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(serviceCollection);
        var anotherClass = CreateInstance<ClassWithStaticCtor>(createFunc, provider: serviceProvider);

        // Assert
        Assert.NotNull(anotherClass);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorWorksWithCtorWithOptionalArgs(CreateInstanceFunc createFunc)
    {
        // Arrange
        var provider = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(provider);

        // Act
        var anotherClass = CreateInstance<ClassWithOptionalArgsCtor>(createFunc, serviceProvider);

        // Assert
        Assert.NotNull(anotherClass);
        Assert.Equal("BLARGH", anotherClass.Whatever);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorWorksWithCtorWithOptionalArgs_WithStructDefaults(CreateInstanceFunc createFunc)
    {
        // Arrange
        var provider = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(provider);

        // Act
        var anotherClass = CreateInstance<ClassWithOptionalArgsCtorWithStructs>(createFunc, serviceProvider);

        // Assert
        Assert.NotNull(anotherClass);
        Assert.Equal(ConsoleColor.DarkGreen, anotherClass.Color);
        Assert.Null(anotherClass.ColorNull);
        Assert.Equal(12, anotherClass.Integer);
        Assert.Null(anotherClass.IntegerNull);
        Assert.Equal(ExpectStructWithPublicDefaultConstructorInvoked, anotherClass.StructWithConstructor.ConstructorInvoked);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorCanDisambiguateConstructorsWithUniqueArguments(CreateInstanceFunc createFunc)
    {
        // Arrange
        var serviceCollection = new TestServiceCollection()
            .AddTransient<IFakeService, FakeService>();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        // Act
        var instance = CreateInstance<ClassWithAmbiguousCtors>(createFunc, serviceProvider, "1", 2);

        // Assert
        Assert.NotNull(instance);
        Assert.NotNull(instance.FakeService);
        Assert.Equal("1", instance.Data1);
        Assert.Equal(2, instance.Data2);
    }

    public static IEnumerable<object[]> TypesWithNonPublicConstructorData =>
        CreateInstanceFuncs.Zip(
                new[] { typeof(ClassWithPrivateCtor), typeof(ClassWithInternalConstructor), typeof(ClassWithProtectedConstructor), typeof(StaticConstructorClass) },
                (a, b) => new object[] { a[0], b });

    [Theory]
    [MemberData(nameof(TypesWithNonPublicConstructorData))]
    public void TypeActivatorRequiresPublicConstructor(CreateInstanceFunc createFunc, Type type)
    {
        // Arrange
        var expectedMessage = $"A suitable constructor for type '{type}' could not be located. " +
            "Ensure the type is concrete and all parameters of a public constructor are either registered as services or passed as arguments. Also ensure no extraneous arguments are provided.";
        var serviceCollection = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        // Act and Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            createFunc(provider: serviceProvider, type: type, args: Array.Empty<object>()));

        Assert.Equal(expectedMessage, ex.Message);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorRequiresAllArgumentsCanBeAccepted(CreateInstanceFunc createFunc)
    {
        // Arrange
        var expectedMessage = $"A suitable constructor for type '{typeof(AnotherClassAcceptingData).FullName}' could not be located. " +
            "Ensure the type is concrete and all parameters of a public constructor are either registered as services or passed as arguments. Also ensure no extraneous arguments are provided.";
        var serviceCollection = new TestServiceCollection()
            .AddTransient<IFakeService, FakeService>();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        var ex1 = Assert.Throws<InvalidOperationException>(() =>
            CreateInstance<AnotherClassAcceptingData>(createFunc, serviceProvider, "1", "2", "3"));
        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            CreateInstance<AnotherClassAcceptingData>(createFunc, serviceProvider, 1, 2));

        Assert.Equal(expectedMessage, ex1.Message);
        Assert.Equal(expectedMessage, ex2.Message);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorRethrowsOriginalExceptionFromConstructor(CreateInstanceFunc createFunc)
    {
        // Act
        var serviceCollection = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        var ex1 = Assert.Throws<Exception>(() =>
            CreateInstance<ClassWithThrowingEmptyCtor>(createFunc, provider: serviceProvider));

        var ex2 = Assert.Throws<Exception>(() =>
            CreateInstance<ClassWithThrowingCtor>(createFunc, provider: serviceProvider, args: new object[] { new FakeService() }));

        // Assert
        Assert.Equal(nameof(ClassWithThrowingEmptyCtor), ex1.Message);
        Assert.Equal(nameof(ClassWithThrowingCtor), ex2.Message);
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    public void TypeActivatorCreateFactoryDoesNotAllowForAmbiguousConstructorMatches(Type paramType)
    {
        // Arrange
        var type = typeof(ClassWithAmbiguousCtors);
        var expectedMessage = $"Multiple constructors accepting all given argument types have been found in type '{type}'. " +
            "There should only be one applicable constructor.";

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ActivatorUtilities.CreateFactory(type, new[] { paramType }));

        // Assert
        Assert.Equal(expectedMessage, ex.Message);
    }

    [Theory]
    [InlineData("", "IFakeService, string")]
    [InlineData(5, "IFakeService, int")]
    public void TypeActivatorCreateInstanceUsesLongestAvailableConstructor(object value, string ctor)
    {
        // Arrange
        var serviceCollection = new TestServiceCollection();
        serviceCollection.AddSingleton<IFakeService, FakeService>();
        var serviceProvider = CreateServiceProvider(serviceCollection);
        var type = typeof(ClassWithAmbiguousCtors);

        if (SupportsIServiceProviderIsService)
        {
            // Act
            var instance = ActivatorUtilities.CreateInstance(serviceProvider, type, value);

            // Assert
            Assert.Equal(ctor, ((ClassWithAmbiguousCtors)instance).CtorUsed);
        }
        else
        {
            // Act
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ActivatorUtilities.CreateInstance(serviceProvider, type, value));

            // Assert
            Assert.Equal($"Multiple constructors accepting all given argument types have been found in type '{type}'. " +
                "There should only be one applicable constructor.", ex.Message);
        }
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorUsesMarkedConstructor(CreateInstanceFunc createFunc)
    {
        // Arrange
        var serviceCollection = new TestServiceCollection();
        serviceCollection.AddSingleton<IFakeService, FakeService>();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        // Act
        var instance = CreateInstance<ClassWithAmbiguousCtorsAndAttribute>(createFunc, serviceProvider, "hello");

        // Assert
        Assert.Equal("IFakeService, string", instance.CtorUsed);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorThrowsOnMultipleMarkedCtors(CreateInstanceFunc createFunc)
    {
        // Arrange
        var serviceCollection = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => CreateInstance<ClassWithMultipleMarkedCtors>(createFunc, serviceProvider, "hello"));

        // Assert
        Assert.Equal("Multiple constructors were marked with ActivatorUtilitiesConstructorAttribute.", exception.Message);
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void TypeActivatorThrowsWhenMarkedCtorDoesntAcceptArguments(CreateInstanceFunc createFunc)
    {
        // Arrange
        string message = "Constructor marked with ActivatorUtilitiesConstructorAttribute does not accept all given argument types.";
        var serviceCollection = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateInstance<ClassWithAmbiguousCtorsAndAttribute>(createFunc, serviceProvider, 0, "hello"));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void GetServiceOrCreateInstanceRegisteredServiceTransient()
    {
        // Reset the count because test order is not guaranteed
        lock (CreationCountFakeService.InstanceLock)
        {
            CreationCountFakeService.InstanceCount = 0;

            var serviceCollection = new TestServiceCollection()
                .AddTransient<IFakeService, FakeService>()
                .AddTransient<CreationCountFakeService>();

            var serviceProvider = CreateServiceProvider(serviceCollection);

            var service = ActivatorUtilities.GetServiceOrCreateInstance<CreationCountFakeService>(serviceProvider);
            Assert.NotNull(service);
            Assert.Equal(1, service.InstanceId);
            Assert.Equal(1, CreationCountFakeService.InstanceCount);

            service = ActivatorUtilities.GetServiceOrCreateInstance<CreationCountFakeService>(serviceProvider);
            Assert.NotNull(service);
            Assert.Equal(2, service.InstanceId);
            Assert.Equal(2, CreationCountFakeService.InstanceCount);
        }
    }

    [Fact]
    public void GetServiceOrCreateInstanceRegisteredServiceSingleton()
    {
        lock (CreationCountFakeService.InstanceLock)
        {
            // Arrange
            // Reset the count because test order is not guaranteed
            CreationCountFakeService.InstanceCount = 0;

            var serviceCollection = new TestServiceCollection()
                .AddTransient<IFakeService, FakeService>()
                .AddSingleton<CreationCountFakeService>();
            var serviceProvider = CreateServiceProvider(serviceCollection);

            // Act and Assert
            var service = ActivatorUtilities.GetServiceOrCreateInstance<CreationCountFakeService>(serviceProvider);
            Assert.NotNull(service);
            Assert.Equal(1, service.InstanceId);
            Assert.Equal(1, CreationCountFakeService.InstanceCount);

            service = ActivatorUtilities.GetServiceOrCreateInstance<CreationCountFakeService>(serviceProvider);
            Assert.NotNull(service);
            Assert.Equal(1, service.InstanceId);
            Assert.Equal(1, CreationCountFakeService.InstanceCount);
        }
    }

    [Fact]
    public void GetServiceOrCreateInstanceUnregisteredService()
    {
        lock (CreationCountFakeService.InstanceLock)
        {
            // Arrange
            // Reset the count because test order is not guaranteed
            CreationCountFakeService.InstanceCount = 0;

            var serviceCollection = new TestServiceCollection()
                .AddTransient<IFakeService, FakeService>();
            var serviceProvider = CreateServiceProvider(serviceCollection);

            // Act and Assert
            var service = (CreationCountFakeService)ActivatorUtilities.GetServiceOrCreateInstance(
                serviceProvider,
                typeof(CreationCountFakeService));
            Assert.NotNull(service);
            Assert.Equal(1, service.InstanceId);
            Assert.Equal(1, CreationCountFakeService.InstanceCount);

            service = ActivatorUtilities.GetServiceOrCreateInstance<CreationCountFakeService>(serviceProvider);
            Assert.NotNull(service);
            Assert.Equal(2, service.InstanceId);
            Assert.Equal(2, CreationCountFakeService.InstanceCount);
        }
    }

    [Theory]
    [MemberData(nameof(CreateInstanceFuncs))]
    public void UnRegisteredServiceAsConstructorParameterThrowsException(CreateInstanceFunc createFunc)
    {
        var serviceCollection = new TestServiceCollection()
            .AddSingleton<CreationCountFakeService>();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CreateInstance<CreationCountFakeService>(createFunc, serviceProvider));
        Assert.Equal($"Unable to resolve service for type '{typeof(IFakeService)}' while attempting" +
            $" to activate '{typeof(CreationCountFakeService)}'.",
            ex.Message);
    }

    [Fact]
    public void CreateInstance_WithAbstractTypeAndPublicConstructor_ThrowsCorrectException()
    {
        // Arrange
        var serviceCollection = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => ActivatorUtilities.CreateInstance(serviceProvider, typeof(AbstractFoo)));
        var msg = "Instances of abstract classes cannot be created.";
        Assert.Equal(msg, ex.Message);
    }

    [Fact]
    public void CreateInstance_CapturesInnerException_OfTargetInvocationException()
    {
        // Act & Assert
        var serviceCollection = new TestServiceCollection();
        var serviceProvider = CreateServiceProvider(serviceCollection);

        var ex = Assert.Throws<InvalidOperationException>(() => ActivatorUtilities.CreateInstance(serviceProvider, typeof(Bar)));
        var msg = "some error";
        Assert.Equal(msg, ex.Message);
    }

    private abstract class AbstractFoo
    {
        // The constructor should be public, since that is checked as well.
        public AbstractFoo()
        {
        }
    }

    private sealed class Bar
    {
        public Bar()
        {
            throw new InvalidOperationException("some error");
        }
    }

    private class StaticConstructorClass
    {
        static StaticConstructorClass() { }

        private StaticConstructorClass() { }
    }
}
