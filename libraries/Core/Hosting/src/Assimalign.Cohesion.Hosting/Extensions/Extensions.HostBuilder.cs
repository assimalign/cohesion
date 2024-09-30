using System;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

using Assimalign.Cohesion.Hosting.Internal;

public static class HostBuilderExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="method"></param>
    /// <returns></returns>
    public static IHostBuilder AddService(this IHostBuilder builder, Func<IServiceProvider, IHostService> method)
    {
        return builder.AddService(context =>
        {
            if (context.ServiceProvider is null)
            {
                ThrowHelper.ThrowInvalidOperationException("No Service provider was created for the host.");
            }

            return method.Invoke(context.ServiceProvider);
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TService"></typeparam>
    /// <param name="builder"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static IHostBuilder AddService<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TService>(this IHostBuilder builder) 
        where TService : IHostService
    {
        return builder.AddService(context =>
        {
            if (context.ServiceProvider is null)
            {
                ThrowHelper.ThrowInvalidOperationException("");
            }

            var serviceProvider = context.ServiceProvider;

            var type = typeof(TService);
            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);

            // If using DI to instantiate the hosted service the type can only have one public constructure.
            if (constructors.Length > 1)
            {
                // TODO: Throw exception;
            }

            var constructor = constructors[0];
            var parameters = constructor.GetParameters();

            // If only a public parameterless constructure then return instance
            if (parameters.Length == 0)
            {
                return Activator.CreateInstance<TService>();
            }

            var arguments = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var argument = serviceProvider.GetService(parameters[i].ParameterType);

                if (argument is null)
                {
                    throw new Exception("Unable to resolve service");
                }

                arguments[i] = argument;
            }

            var instance = Activator.CreateInstance(type, arguments);

            if (instance is null || instance is not TService service)
            {
                throw new Exception();
            }

            return service;
        });
    }
}
