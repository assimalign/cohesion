using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Assimalign.Cohesion.DependencyInjection.Properties;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class CallSiteFactory : IServiceLookup
{
    private const int DefaultSlot = 0;
    private readonly ServiceDescriptor[]                                            descriptors;
    private readonly Dictionary<Type, ServiceDescriptorCacheItem>                   descriptorLookup   = new();
    private readonly ConcurrentDictionary<CallSiteServiceCacheKey, CallSiteService> callSiteCache      = new();
    private readonly ConcurrentDictionary<Type, object>                             callSiteLocks      = new();
    private readonly CallSiteStackGuard                                             callSiteStackGuard;

    public CallSiteFactory(ICollection<ServiceDescriptor> descriptors)
    {
        this.callSiteStackGuard = new();
        this.descriptors = new ServiceDescriptor[descriptors.Count];
        descriptors.CopyTo(this.descriptors, 0);

        Populate();
    }

    internal ServiceDescriptor[] Descriptors => descriptors;

    private void Populate()
    {
        foreach (ServiceDescriptor descriptor in descriptors)
        {
            var serviceType = descriptor.ServiceType;
            
            if (serviceType.IsGenericTypeDefinition)
            {
                var implementationType = descriptor.ImplementationType;

                if (implementationType == null || !implementationType.IsGenericTypeDefinition)
                {
                    throw new ArgumentException(
                        Resources.GetOpenGenericServiceRequiresOpenGenericImplementationExceptionMessage(serviceType),
                        "descriptors");
                }
                if (implementationType.IsAbstract || implementationType.IsInterface)
                {
                    throw new ArgumentException(
                        Resources.GetTypeCannotBeActivatedExceptionMessage(implementationType, serviceType));
                }
                Type[] serviceTypeGenericArguments = serviceType.GetGenericArguments();
                Type[] implementationTypeGenericArguments = implementationType.GetGenericArguments();
                if (serviceTypeGenericArguments.Length != implementationTypeGenericArguments.Length)
                {
                    throw new ArgumentException(
                        Resources.GetArityOfOpenGenericServiceNotEqualArityOfOpenGenericImplementationExceptionMessage(serviceType, implementationType), "descriptors");
                }
                if (ServiceProvider.VerifyOpenGenericServiceTrimmability)
                {
                    ValidateTrimmingAnnotations(serviceType, serviceTypeGenericArguments, implementationType, implementationTypeGenericArguments);
                }
            }
            else if (descriptor.ImplementationInstance == null && descriptor.ImplementationFactory == null)
            {
                Debug.Assert(descriptor.ImplementationType != null);
                Type implementationType = descriptor.ImplementationType;

                if (implementationType.IsGenericTypeDefinition ||
                    implementationType.IsAbstract ||
                    implementationType.IsInterface)
                {
                    throw new ArgumentException(
                         Resources.GetTypeCannotBeActivatedExceptionMessage(implementationType, serviceType));
                }
            }

            Type cacheKey = serviceType;
            descriptorLookup.TryGetValue(cacheKey, out ServiceDescriptorCacheItem cacheItem);
            descriptorLookup[cacheKey] = cacheItem.Add(descriptor);
        }
    }

    /// <summary>
    /// Validates that two generic type definitions have compatible trimming annotations on their generic arguments.
    /// </summary>
    /// <remarks>
    /// When open generic types are used in DI, there is an error when the concrete implementation type
    /// has [DynamicallyAccessedMembers] attributes on a generic argument type, but the interface/service type
    /// doesn't have matching annotations. The problem is that the trimmer doesn't see the members that need to
    /// be preserved on the type being passed to the generic argument. But when the interface/service type also has
    /// the annotations, the trimmer will see which members need to be preserved on the closed generic argument type.
    /// </remarks>
    private static void ValidateTrimmingAnnotations(
        Type serviceType,
        Type[] serviceTypeGenericArguments,
        Type implementationType,
        Type[] implementationTypeGenericArguments)
    {
        Debug.Assert(serviceTypeGenericArguments.Length == implementationTypeGenericArguments.Length);

        for (int i = 0; i < serviceTypeGenericArguments.Length; i++)
        {
            Type serviceGenericType = serviceTypeGenericArguments[i];
            Type implementationGenericType = implementationTypeGenericArguments[i];

            DynamicallyAccessedMemberTypes serviceDynamicallyAccessedMembers = GetDynamicallyAccessedMemberTypes(serviceGenericType);
            DynamicallyAccessedMemberTypes implementationDynamicallyAccessedMembers = GetDynamicallyAccessedMemberTypes(implementationGenericType);

            if (!AreCompatible(serviceDynamicallyAccessedMembers, implementationDynamicallyAccessedMembers))
            {
                throw new ArgumentException(
                    Resources.GetTrimmingAnnotationsDoNotMatchExceptionMessage(implementationType, serviceType));
            }

            bool serviceHasNewConstraint = serviceGenericType.GenericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint);
            bool implementationHasNewConstraint = implementationGenericType.GenericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint);
            if (implementationHasNewConstraint && !serviceHasNewConstraint)
            {
                throw new ArgumentException(
                    Resources.GetTrimmingAnnotationsDoNotMatch_NewConstraintExceptionMessage(implementationType, serviceType));
            }
        }
    }

    private static DynamicallyAccessedMemberTypes GetDynamicallyAccessedMemberTypes(Type serviceGenericType)
    {
        foreach (CustomAttributeData attributeData in serviceGenericType.GetCustomAttributesData())
        {
            if (attributeData.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute" &&
                attributeData.ConstructorArguments.Count == 1 &&
                attributeData.ConstructorArguments[0].ArgumentType.FullName == "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes")
            {
                return (DynamicallyAccessedMemberTypes)(int)attributeData.ConstructorArguments[0].Value;
            }
        }

        return DynamicallyAccessedMemberTypes.None;
    }

    private static bool AreCompatible(
        DynamicallyAccessedMemberTypes serviceDynamicallyAccessedMembers, 
        DynamicallyAccessedMemberTypes implementationDynamicallyAccessedMembers)
    {
        // The DynamicallyAccessedMemberTypes don't need to exactly match.
        // The service type needs to preserve a superset of the members required by the implementation type.
        return serviceDynamicallyAccessedMembers.HasFlag(implementationDynamicallyAccessedMembers);
    }

    // For unit testing
    internal int? GetSlot(ServiceDescriptor serviceDescriptor)
    {
        if (descriptorLookup.TryGetValue(serviceDescriptor.ServiceType, out ServiceDescriptorCacheItem item))
        {
            return item.GetSlot(serviceDescriptor);
        }

        return null;
    }

    internal CallSiteService GetCallSite(Type serviceType, CallSiteChain callSiteChain) =>
            callSiteCache.TryGetValue(new CallSiteServiceCacheKey(serviceType, DefaultSlot), out CallSiteService? site) ? site :
        CreateCallSite(serviceType, callSiteChain);

    internal CallSiteService GetCallSite(ServiceDescriptor serviceDescriptor, CallSiteChain callSiteChain)
    {
        if (descriptorLookup.TryGetValue(serviceDescriptor.ServiceType, out ServiceDescriptorCacheItem descriptor))
        {
            return TryCreateExact(serviceDescriptor, serviceDescriptor.ServiceType, callSiteChain, descriptor.GetSlot(serviceDescriptor));
        }

        Debug.Fail("descriptorLookup didn't contain requested serviceDescriptor");
        return null;
    }

    private CallSiteService CreateCallSite(Type serviceType, CallSiteChain callSiteChain)
    {
        if (!callSiteStackGuard.TryEnterOnCurrentStack())
        {
            return callSiteStackGuard.RunOnEmptyStack((type, chain) => CreateCallSite(type, chain), serviceType, callSiteChain);
        }

        // We need to lock the resolution process for a single service type at a time:
        // Consider the following:
        // C -> D -> A
        // E -> D -> A
        // Resolving C and E in parallel means that they will be modifying the callsite cache concurrently
        // to add the entry for C and E, but the resolution of D and A is synchronized
        // to make sure C and E both reference the same instance of the callsite.

        // This is to make sure we can safely store singleton values on the callsites themselves

        var callsiteLock = callSiteLocks.GetOrAdd(serviceType, static _ => new object());

        lock (callsiteLock)
        {
            callSiteChain.CheckCircularDependency(serviceType);

            CallSiteService callSite = TryCreateExact(serviceType, callSiteChain) ??
                                       TryCreateOpenGeneric(serviceType, callSiteChain) ??
                                       TryCreateEnumerable(serviceType, callSiteChain);

            return callSite;
        }
    }

    private CallSiteService TryCreateExact(Type serviceType, CallSiteChain callSiteChain)
    {
        if (descriptorLookup.TryGetValue(serviceType, out var descriptor))
        {
            return TryCreateExact(descriptor.Last, serviceType, callSiteChain, DefaultSlot);
        }

        return null;
    }

    private CallSiteService TryCreateOpenGeneric(Type serviceType, CallSiteChain callSiteChain)
    {
        if (serviceType.IsConstructedGenericType
            && descriptorLookup.TryGetValue(serviceType.GetGenericTypeDefinition(), out ServiceDescriptorCacheItem descriptor))
        {
            return TryCreateOpenGeneric(descriptor.Last, serviceType, callSiteChain, DefaultSlot, true);
        }

        return null;
    }

    private CallSiteService TryCreateEnumerable(Type serviceType, CallSiteChain callSiteChain)
    {
        CallSiteServiceCacheKey callSiteKey = new CallSiteServiceCacheKey(serviceType, DefaultSlot);
        if (callSiteCache.TryGetValue(callSiteKey, out CallSiteService serviceCallSite))
        {
            return serviceCallSite;
        }

        try
        {
            callSiteChain.Add(serviceType);

            if (serviceType.IsConstructedGenericType &&
                serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                Type itemType = serviceType.GenericTypeArguments[0];
                CallSiteResultCacheLocation cacheLocation = CallSiteResultCacheLocation.Root;

                var callSites = new List<CallSiteService>();

                // If item type is not generic we can safely use descriptor cache
                if (!itemType.IsConstructedGenericType &&
                    descriptorLookup.TryGetValue(itemType, out ServiceDescriptorCacheItem descriptors))
                {
                    for (int i = 0; i < descriptors.Count; i++)
                    {
                        ServiceDescriptor descriptor = descriptors[i];

                        // Last service should get slot 0
                        int slot = descriptors.Count - i - 1;
                        // There may not be any open generics here
                        CallSiteService callSite = TryCreateExact(descriptor, itemType, callSiteChain, slot);
                        Debug.Assert(callSite != null);

                        cacheLocation = GetCommonCacheLocation(cacheLocation, callSite.Cache.Location);
                        callSites.Add(callSite);
                    }
                }
                else
                {
                    int slot = 0;
                    // We are going in reverse so the last service in descriptor list gets slot 0
                    for (int i = this.descriptors.Length - 1; i >= 0; i--)
                    {
                        ServiceDescriptor descriptor = this.descriptors[i];
                        CallSiteService callSite = TryCreateExact(descriptor, itemType, callSiteChain, slot) ??
                                       TryCreateOpenGeneric(descriptor, itemType, callSiteChain, slot, false);

                        if (callSite != null)
                        {
                            slot++;

                            cacheLocation = GetCommonCacheLocation(cacheLocation, callSite.Cache.Location);
                            callSites.Add(callSite);
                        }
                    }

                    callSites.Reverse();
                }


                CallSiteResultCache resultCache = CallSiteResultCache.None;
                if (cacheLocation == CallSiteResultCacheLocation.Scope || cacheLocation == CallSiteResultCacheLocation.Root)
                {
                    resultCache = new CallSiteResultCache(cacheLocation, callSiteKey);
                }

                return callSiteCache[callSiteKey] = new EnumerableCallSite(resultCache, itemType, callSites.ToArray());
            }

            return null;
        }
        finally
        {
            callSiteChain.Remove(serviceType);
        }
    }

    private CallSiteResultCacheLocation GetCommonCacheLocation(CallSiteResultCacheLocation locationA, CallSiteResultCacheLocation locationB)
    {
        return (CallSiteResultCacheLocation)Math.Max((int)locationA, (int)locationB);
    }

    private CallSiteService TryCreateExact(ServiceDescriptor descriptor, Type serviceType, CallSiteChain callSiteChain, int slot)
    {
        if (serviceType == descriptor.ServiceType)
        {
            CallSiteServiceCacheKey callSiteKey = new CallSiteServiceCacheKey(serviceType, slot);
            if (callSiteCache.TryGetValue(callSiteKey, out CallSiteService serviceCallSite))
            {
                return serviceCallSite;
            }

            CallSiteService callSite;
            var lifetime = new CallSiteResultCache(descriptor.Lifetime, serviceType, slot);
            if (descriptor.ImplementationInstance != null)
            {
                callSite = new ConstantCallSite(descriptor.ServiceType, descriptor.ImplementationInstance);
            }
            else if (descriptor.ImplementationFactory != null)
            {
                callSite = new FactoryCallSite(lifetime, descriptor.ServiceType, descriptor.ImplementationFactory);
            }
            else if (descriptor.ImplementationType != null)
            {
                callSite = CreateConstructorCallSite(lifetime, descriptor.ServiceType, descriptor.ImplementationType, callSiteChain);
            }
            else
            {
                throw new InvalidOperationException(Resources.InvalidServiceDescriptor);
            }

            return callSiteCache[callSiteKey] = callSite;
        }

        return null;
    }

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2055:MakeGenericType",
        Justification = "MakeGenericType here is used to create a closed generic implementation type given the closed service type. " +
        "Trimming annotations on the generic types are verified when 'Assimalign.Cohesion.DependencyInjection.VerifyOpenGenericServiceTrimmability' is set, which is set by default when PublishTrimmed=true. " +
        "That check informs developers when these generic types don't have compatible trimming annotations.")]
    private CallSiteService TryCreateOpenGeneric(ServiceDescriptor descriptor, Type serviceType, CallSiteChain callSiteChain, int slot, bool throwOnConstraintViolation)
    {
        if (serviceType.IsConstructedGenericType &&
            serviceType.GetGenericTypeDefinition() == descriptor.ServiceType)
        {
            CallSiteServiceCacheKey callSiteKey = new CallSiteServiceCacheKey(serviceType, slot);
            if (callSiteCache.TryGetValue(callSiteKey, out CallSiteService serviceCallSite))
            {
                return serviceCallSite;
            }

            Debug.Assert(descriptor.ImplementationType != null, "descriptor.ImplementationType != null");
            var lifetime = new CallSiteResultCache(descriptor.Lifetime, serviceType, slot);
            Type closedType;
            try
            {
                closedType = descriptor.ImplementationType.MakeGenericType(serviceType.GenericTypeArguments);
            }
            catch (ArgumentException)
            {
                if (throwOnConstraintViolation)
                {
                    throw;
                }

                return null;
            }

            return callSiteCache[callSiteKey] = CreateConstructorCallSite(lifetime, serviceType, closedType, callSiteChain);
        }

        return null;
    }

    private CallSiteService CreateConstructorCallSite(
        CallSiteResultCache lifetime,
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
        CallSiteChain callSiteChain)
    {
        try
        {
            callSiteChain.Add(serviceType, implementationType);
            ConstructorInfo[] constructors = implementationType.GetConstructors();

            CallSiteService[] parameterCallSites = null;

            if (constructors.Length == 0)
            {
                throw new InvalidOperationException(Resources.GetNoConstructorMatchExceptionMessage(implementationType));
            }
            else if (constructors.Length == 1)
            {
                ConstructorInfo constructor = constructors[0];
                ParameterInfo[] parameters = constructor.GetParameters();
                if (parameters.Length == 0)
                {
                    return new ConstructorCallSite(lifetime, serviceType, constructor);
                }

                parameterCallSites = CreateArgumentCallSites(
                    implementationType,
                    callSiteChain,
                    parameters,
                    throwIfCallSiteNotFound: true);

                return new ConstructorCallSite(lifetime, serviceType, constructor, parameterCallSites);
            }

            Array.Sort(constructors,
                (a, b) => b.GetParameters().Length.CompareTo(a.GetParameters().Length));

            ConstructorInfo bestConstructor = null;
            HashSet<Type> bestConstructorParameterTypes = null;
            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] parameters = constructors[i].GetParameters();

                CallSiteService[] currentParameterCallSites = CreateArgumentCallSites(
                    implementationType,
                    callSiteChain,
                    parameters,
                    throwIfCallSiteNotFound: false);

                if (currentParameterCallSites != null)
                {
                    if (bestConstructor == null)
                    {
                        bestConstructor = constructors[i];
                        parameterCallSites = currentParameterCallSites;
                    }
                    else
                    {
                        // Since we're visiting constructors in decreasing order of number of parameters,
                        // we'll only see ambiguities or supersets once we've seen a 'bestConstructor'.

                        if (bestConstructorParameterTypes == null)
                        {
                            bestConstructorParameterTypes = new HashSet<Type>();
                            foreach (ParameterInfo p in bestConstructor.GetParameters())
                            {
                                bestConstructorParameterTypes.Add(p.ParameterType);
                            }
                        }

                        foreach (ParameterInfo p in parameters)
                        {
                            if (!bestConstructorParameterTypes.Contains(p.ParameterType))
                            {
                                // Ambiguous match exception
                                throw new InvalidOperationException(string.Join(
                                    Environment.NewLine,
                                    Resources.GetAmbiguousConstructorExceptionMessage(implementationType),
                                    bestConstructor,
                                    constructors[i]));
                            }
                        }
                    }
                }
            }

            if (bestConstructor == null)
            {
                throw new InvalidOperationException(
                    Resources.GetUnableToActivateTypeExceptionMessage(implementationType));
            }
            else
            {
                Debug.Assert(parameterCallSites != null);
                return new ConstructorCallSite(lifetime, serviceType, bestConstructor, parameterCallSites);
            }
        }
        finally
        {
            callSiteChain.Remove(serviceType);
        }
    }

    private CallSiteService[] CreateArgumentCallSites(
        Type implementationType,
        CallSiteChain callSiteChain,
        ParameterInfo[] parameters,
        bool throwIfCallSiteNotFound)
    {
        var parameterCallSites = new CallSiteService[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            Type parameterType = parameters[index].ParameterType;
            CallSiteService callSite = GetCallSite(parameterType, callSiteChain);

            if (callSite == null && parameters[index].TryGetDefaultValue(out object defaultValue))
            {
                callSite = new ConstantCallSite(parameterType, defaultValue);
            }

            if (callSite == null)
            {
                if (throwIfCallSiteNotFound)
                {
                    throw new InvalidOperationException(Resources.GetCannotResolveServiceExceptionMessage(
                        parameterType,
                        implementationType));
                }

                return null;
            }

            parameterCallSites[index] = callSite;
        }

        return parameterCallSites;
    }


    public void Add(Type type, CallSiteService serviceCallSite)
    {
        callSiteCache[new CallSiteServiceCacheKey(type, DefaultSlot)] = serviceCallSite;
    }

    public bool IsService(Type serviceType)
    {
        if (serviceType is null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        // Querying for an open generic should return false (they aren't resolvable)
        if (serviceType.IsGenericTypeDefinition)
        {
            return false;
        }

        if (descriptorLookup.ContainsKey(serviceType))
        {
            return true;
        }

        if (serviceType.IsConstructedGenericType && serviceType.GetGenericTypeDefinition() is Type genericDefinition)
        {
            // We special case Enumerable since it isn't explicitly registered in the container
            // yet we can manifest instances of it when requested.
            return genericDefinition == typeof(IEnumerable<>) || descriptorLookup.ContainsKey(genericDefinition);
        }

        // These are the built in service types that aren't part of the list of service descriptors
        // If you update these make sure to also update the code in ServiceProvider.ctor
        return serviceType == typeof(IServiceProvider) ||
               serviceType == typeof(IServiceScopeFactory) ||
               serviceType == typeof(IServiceLookup);
    }

    private struct ServiceDescriptorCacheItem
    {
        private ServiceDescriptor _item;

        private List<ServiceDescriptor> _items;

        public ServiceDescriptor Last
        {
            get
            {
                if (_items != null && _items.Count > 0)
                {
                    return _items[_items.Count - 1];
                }

                Debug.Assert(_item != null);
                return _item;
            }
        }

        public int Count
        {
            get
            {
                if (_item == null)
                {
                    Debug.Assert(_items == null);
                    return 0;
                }

                return 1 + (_items?.Count ?? 0);
            }
        }

        public ServiceDescriptor this[int index]
        {
            get
            {
                if (index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                if (index == 0)
                {
                    return _item;
                }

                return _items[index - 1];
            }
        }

        public int GetSlot(ServiceDescriptor descriptor)
        {
            if (descriptor == _item)
            {
                return Count - 1;
            }

            if (_items != null)
            {
                int index = _items.IndexOf(descriptor);
                if (index != -1)
                {
                    return _items.Count - (index + 1);
                }
            }

            throw new InvalidOperationException(Resources.ServiceDescriptorNotExist);
        }

        public ServiceDescriptorCacheItem Add(ServiceDescriptor descriptor)
        {
            var newCacheItem = default(ServiceDescriptorCacheItem);
            if (_item == null)
            {
                Debug.Assert(_items == null);
                newCacheItem._item = descriptor;
            }
            else
            {
                newCacheItem._item = _item;
                newCacheItem._items = _items ?? new List<ServiceDescriptor>();
                newCacheItem._items.Add(descriptor);
            }
            return newCacheItem;
        }
    }
}
