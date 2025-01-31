﻿using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;


namespace Assimalign.Cohesion.DependencyInjection.Utilities
{
    using Assimalign.Cohesion.DependencyInjection.Internal;

    /// <summary>
    /// Helper code for the various activator services.
    /// </summary>
    public static class ActivatorUtilities
    {
        private static readonly MethodInfo GetServiceInfo =
            GetMethodInfo<Func<IServiceProvider, Type, Type, bool, object?>>((sp, t, r, c) => GetService(sp, t, r, c));



        /// <summary>
        /// Create a delegate that will instantiate a type with constructor arguments provided directly
        /// and/or from an <see cref="IServiceProvider"/>.
        /// </summary>
        /// <param name="instanceType">The type to activate</param>
        /// <param name="argumentTypes">
        /// The types of objects, in order, that will be passed to the returned function as its second parameter
        /// </param>
        /// <returns>
        /// A factory that will instantiate instanceType using an <see cref="IServiceProvider"/>
        /// and an argument array containing objects matching the types defined in argumentTypes
        /// </returns>
        public static ObjectFactory CreateFactory([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type instanceType, Type[] argumentTypes)
        {
            CreateFactoryInternal(
                instanceType,
                argumentTypes,
                out ParameterExpression provider,
                out ParameterExpression argumentArray,
                out Expression factoryExpressionBody);

            var factoryLambda = Expression.Lambda<Func<IServiceProvider, object?[]?, object>>(
                factoryExpressionBody,
                provider,
                argumentArray);

            Func<IServiceProvider, object?[]?, object>? result = factoryLambda.Compile();

            return result.Invoke;
        }

        /// <summary>
        /// Create a delegate that will instantiate a type with constructor arguments provided directly
        /// and/or from an <see cref="IServiceProvider"/>.
        /// </summary>
        /// <typeparam name="T">The type to activate</typeparam>
        /// <param name="argumentTypes">
        /// The types of objects, in order, that will be passed to the returned function as its second parameter
        /// </param>
        /// <returns>
        /// A factory that will instantiate type T using an <see cref="IServiceProvider"/>
        /// and an argument array containing objects matching the types defined in argumentTypes
        /// </returns>
        public static ObjectFactory<T> CreateFactory<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Type[] argumentTypes)
        {
            CreateFactoryInternal(
                typeof(T),
                argumentTypes,
                out ParameterExpression provider,
                out ParameterExpression argumentArray,
                out Expression factoryExpressionBody);

            var factoryLambda = Expression.Lambda<Func<IServiceProvider, object?[]?, T>>(
                factoryExpressionBody,
                provider,
                argumentArray);

            Func<IServiceProvider, object?[]?, T>? result = factoryLambda.Compile();

            return result.Invoke;
        }

        private static void CreateFactoryInternal([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type instanceType, Type[] argumentTypes, out ParameterExpression provider, out ParameterExpression argumentArray, out Expression factoryExpressionBody)
        {
            FindApplicableConstructor(
                instanceType,
                argumentTypes,
                out ConstructorInfo constructor,
                out int?[] parameterMap);

            provider = Expression.Parameter(typeof(IServiceProvider), "provider");
            argumentArray = Expression.Parameter(typeof(object[]), "argumentArray");
            factoryExpressionBody = BuildFactoryExpression(constructor, parameterMap, provider, argumentArray);
        }


        /// <summary>
        /// Instantiate a type with constructor arguments provided directly and/or from an <see cref="IServiceProvider"/>.
        /// </summary>
        /// <param name="provider">The service provider used to resolve dependencies</param>
        /// <param name="instanceType">The type to activate</param>
        /// <param name="parameters">Constructor arguments not provided by the <paramref name="provider"/>.</param>
        /// <returns>An activated object of type instanceType</returns>
        public static object CreateInstance(IServiceProvider provider, Type instanceType, params object[] parameters)
        {
            int bestLength = -1;
            bool seenPreferred = false;

            ConstructorMatcher bestMatcher = default;

            if (!instanceType.IsAbstract)
            {
                foreach (ConstructorInfo? constructor in instanceType.GetConstructors())
                {
                    var matcher = new ConstructorMatcher(constructor);
                    var isPreferred = constructor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), false);
                    var length = matcher.Match(parameters);

                    if (isPreferred)
                    {
                        if (seenPreferred)
                        {
                            ThrowMultipleCtorsMarkedWithAttributeException();
                        }
                        if (length == -1)
                        {
                            ThrowMarkedCtorDoesNotTakeAllProvidedArguments();
                        }
                    }
                    if (isPreferred || bestLength < length)
                    {
                        bestLength = length;
                        bestMatcher = matcher;
                    }

                    seenPreferred |= isPreferred;
                }
            }

            if (bestLength == -1)
            {
                string? message = $"A suitable constructor for type '{instanceType}' could not be located. Ensure the type is concrete and all parameters of a public constructor are either registered as services or passed as arguments. Also ensure no extraneous arguments are provided.";
                throw new InvalidOperationException(message);
            }

            return bestMatcher.CreateInstance(provider);
        }

        /// <summary>
        /// Instantiate a type with constructor arguments provided directly and/or from an <see cref="IServiceProvider"/>.
        /// </summary>
        /// <typeparam name="T">The type to activate</typeparam>
        /// <param name="provider">The service provider used to resolve dependencies</param>
        /// <param name="parameters">Constructor arguments not provided by the <paramref name="provider"/>.</param>
        /// <returns>An activated object of type T</returns>
        public static T CreateInstance<T>(IServiceProvider provider, params object[] parameters)
        {
            return (T)CreateInstance(provider, typeof(T), parameters);
        }

        /// <summary>
        /// Retrieve an instance of the given type from the service provider. If one is not found then instantiate it directly.
        /// </summary>
        /// <typeparam name="T">The type of the service</typeparam>
        /// <param name="provider">The service provider used to resolve dependencies</param>
        /// <returns>The resolved service or created instance</returns>
        public static T GetServiceOrCreateInstance<T>(IServiceProvider provider)
        {
            return (T)GetServiceOrCreateInstance(provider, typeof(T));
        }

        /// <summary>
        /// Retrieve an instance of the given type from the service provider. If one is not found then instantiate it directly.
        /// </summary>
        /// <param name="provider">The service provider</param>
        /// <param name="type">The type of the service</param>
        /// <returns>The resolved service or created instance</returns>
        public static object GetServiceOrCreateInstance(IServiceProvider provider, Type type)
        {
            return provider.GetService(type) ?? CreateInstance(provider, type);
        }

        private static MethodInfo GetMethodInfo<T>(Expression<T> expr)
        {
            var mc = (MethodCallExpression)expr.Body;
            return mc.Method;
        }
        private static object? GetService(IServiceProvider sp, Type type, Type requiredBy, bool isDefaultParameterRequired)
        {
            object? service = sp.GetService(type);
            if (service == null && !isDefaultParameterRequired)
            {
                string? message = $"Unable to resolve service for type '{type}' while attempting to activate '{requiredBy}'.";
                throw new InvalidOperationException(message);
            }
            return service;
        }
        private static Expression BuildFactoryExpression(
            ConstructorInfo constructor,
            int?[] parameterMap,
            Expression serviceProvider,
            Expression factoryArgumentArray)
        {
            var constructorParameters = constructor.GetParameters();
            var constructorArguments = new Expression[constructorParameters.Length];

            for (int i = 0; i < constructorParameters.Length; i++)
            {
                var constructorParameter = constructorParameters[i];
                var parameterType = constructorParameter.ParameterType;
                var hasDefaultValue = constructorParameter.TryGetDefaultValue(out object? defaultValue);

                constructorArguments[i] = parameterMap[i] != null ?
                    Expression.ArrayAccess(factoryArgumentArray, Expression.Constant(parameterMap[i])) :
                    Expression.Call(GetServiceInfo, new Expression[]
                    {
                        serviceProvider,
                        Expression.Constant(parameterType, typeof(Type)),
                        Expression.Constant(constructor.DeclaringType, typeof(Type)),
                        Expression.Constant(hasDefaultValue)
                    });

                // Support optional constructor arguments by passing in the default value
                // when the argument would otherwise be null.
                if (hasDefaultValue)
                {
                    constructorArguments[i] = Expression.Coalesce(
                        constructorArguments[i], 
                        Expression.Constant(defaultValue));
                }

                constructorArguments[i] = Expression.Convert(constructorArguments[i], parameterType);
            }

            return Expression.New(constructor, constructorArguments);
        }

        private static void FindApplicableConstructor(
            Type instanceType,
            Type[] argumentTypes,
            out ConstructorInfo matchingConstructor,
            out int?[] matchingParameterMap)
        {
            ConstructorInfo? constructorInfo = null;
            int?[]? parameterMap = null;

            if (!TryFindPreferredConstructor(instanceType, argumentTypes, ref constructorInfo, ref parameterMap) &&
                !TryFindMatchingConstructor(instanceType, argumentTypes, ref constructorInfo, ref parameterMap))
            {
                string? message = $"A suitable constructor for type '{instanceType}' could not be located. Ensure the type is concrete and all parameters of a public constructor are either registered as services or passed as arguments. Also ensure no extraneous arguments are provided.";
                throw new InvalidOperationException(message);
            }

            matchingConstructor = constructorInfo;
            matchingParameterMap = parameterMap;
        }

        // Tries to find constructor based on provided argument types
        private static bool TryFindMatchingConstructor(
            Type instanceType,
            Type[] argumentTypes,
            [NotNullWhen(true)] ref ConstructorInfo? matchingConstructor,
            [NotNullWhen(true)] ref int?[]? parameterMap)
        {
            foreach (ConstructorInfo? constructor in instanceType.GetConstructors())
            {
                if (TryCreateParameterMap(constructor.GetParameters(), argumentTypes, out int?[] tempParameterMap))
                {
                    if (matchingConstructor != null)
                    {
                        throw new InvalidOperationException($"Multiple constructors accepting all given argument types have been found in type '{instanceType}'. There should only be one applicable constructor.");
                    }

                    matchingConstructor = constructor;
                    parameterMap = tempParameterMap;
                }
            }

            if (matchingConstructor != null)
            {
                Debug.Assert(parameterMap != null);
                return true;
            }

            return false;
        }

        // Tries to find constructor marked with ActivatorUtilitiesConstructorAttribute
        private static bool TryFindPreferredConstructor(
            Type instanceType,
            Type[] argumentTypes,
            [NotNullWhen(true)] ref ConstructorInfo? matchingConstructor,
            [NotNullWhen(true)] ref int?[]? parameterMap)
        {
            bool seenPreferred = false;
            foreach (ConstructorInfo? constructor in instanceType.GetConstructors())
            {
                if (constructor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), false))
                {
                    if (seenPreferred)
                    {
                        ThrowMultipleCtorsMarkedWithAttributeException();
                    }
                    if (!TryCreateParameterMap(constructor.GetParameters(), argumentTypes, out int?[] tempParameterMap))
                    {
                        ThrowMarkedCtorDoesNotTakeAllProvidedArguments();
                    }

                    matchingConstructor = constructor;
                    parameterMap = tempParameterMap;
                    seenPreferred = true;
                }
            }

            if (matchingConstructor != null)
            {
                Debug.Assert(parameterMap != null);
                return true;
            }

            return false;
        }

        // Creates an injective parameterMap from givenParameterTypes to assignable constructorParameters.
        // Returns true if each given parameter type is assignable to a unique; otherwise, false.
        private static bool TryCreateParameterMap(ParameterInfo[] constructorParameters, Type[] argumentTypes, out int?[] parameterMap)
        {
            parameterMap = new int?[constructorParameters.Length];

            for (int i = 0; i < argumentTypes.Length; i++)
            {
                bool foundMatch = false;
                Type? givenParameter = argumentTypes[i];

                for (int j = 0; j < constructorParameters.Length; j++)
                {
                    if (parameterMap[j] != null)
                    {
                        // This ctor parameter has already been matched
                        continue;
                    }

                    if (constructorParameters[j].ParameterType.IsAssignableFrom(givenParameter))
                    {
                        foundMatch = true;
                        parameterMap[j] = i;
                        break;
                    }
                }

                if (!foundMatch)
                {
                    return false;
                }
            }

            return true;
        }

        private struct ConstructorMatcher
        {
            private readonly ConstructorInfo _constructor;
            private readonly ParameterInfo[] _parameters;
            private readonly object?[] _parameterValues;

            public ConstructorMatcher(ConstructorInfo constructor)
            {
                _constructor = constructor;
                _parameters = _constructor.GetParameters();
                _parameterValues = new object?[_parameters.Length];
            }

            public int Match(object[] givenParameters)
            {
                int applyIndexStart = 0;
                int applyExactLength = 0;
                for (int givenIndex = 0; givenIndex != givenParameters.Length; givenIndex++)
                {
                    Type? givenType = givenParameters[givenIndex]?.GetType();
                    bool givenMatched = false;

                    for (int applyIndex = applyIndexStart; givenMatched == false && applyIndex != _parameters.Length; ++applyIndex)
                    {
                        if (_parameterValues[applyIndex] == null &&
                            _parameters[applyIndex].ParameterType.IsAssignableFrom(givenType))
                        {
                            givenMatched = true;
                            _parameterValues[applyIndex] = givenParameters[givenIndex];
                            if (applyIndexStart == applyIndex)
                            {
                                applyIndexStart++;
                                if (applyIndex == givenIndex)
                                {
                                    applyExactLength = applyIndex;
                                }
                            }
                        }
                    }

                    if (givenMatched == false)
                    {
                        return -1;
                    }
                }
                return applyExactLength;
            }

            public object CreateInstance(IServiceProvider provider)
            {
                for (int index = 0; index != _parameters.Length; index++)
                {
                    if (_parameterValues[index] == null)
                    {
                        object? value = provider.GetService(_parameters[index].ParameterType);
                        if (value == null)
                        {
                            if (!_parameters[index].TryGetDefaultValue(out object? defaultValue))
                            {
                                throw new InvalidOperationException($"Unable to resolve service for type '{_parameters[index].ParameterType}' while attempting to activate '{_constructor.DeclaringType}'.");
                            }
                            else
                            {
                                _parameterValues[index] = defaultValue;
                            }
                        }
                        else
                        {
                            _parameterValues[index] = value;
                        }
                    }
                }

                return _constructor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, parameters: _parameterValues, culture: null);
            }
        }

        private static void ThrowMultipleCtorsMarkedWithAttributeException()
        {
            throw new InvalidOperationException($"Multiple constructors were marked with {nameof(ActivatorUtilitiesConstructorAttribute)}.");
        }

        private static void ThrowMarkedCtorDoesNotTakeAllProvidedArguments()
        {
            throw new InvalidOperationException($"Constructor marked with {nameof(ActivatorUtilitiesConstructorAttribute)} does not accept all given argument types.");
        }
    }
}
