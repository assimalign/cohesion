using System;
using System.Text;
using System.Collections.Generic;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

using Assimalign.Cohesion.DependencyInjection.Properties;

internal sealed class CallSiteChain
{
    private readonly Dictionary<Type, ChainItemInfo> callSiteChain;

    public CallSiteChain()
    {
        callSiteChain = new Dictionary<Type, ChainItemInfo>();
    }

    public void CheckCircularDependency(Type serviceType)
    {
        if (callSiteChain.ContainsKey(serviceType))
        {
            throw new InvalidOperationException(CreateCircularDependencyExceptionMessage(serviceType));
        }
    }
    public void Remove(Type serviceType) => callSiteChain.Remove(serviceType);
    public void Add(Type serviceType, Type implementationType = null) => callSiteChain[serviceType] = new ChainItemInfo(callSiteChain.Count, implementationType);
    private string CreateCircularDependencyExceptionMessage(Type type)
    {
        var messageBuilder = new StringBuilder()
            .Append(Resources.GetCircularDependencyExceptionMessage(TypeNameHelper.GetTypeDisplayName(type)))
            .AppendLine();

        AppendResolutionPath(messageBuilder, type);

        return messageBuilder.ToString();
    }
    private void AppendResolutionPath(StringBuilder builder, Type currentlyResolving)
    {
        var ordered = new List<KeyValuePair<Type, ChainItemInfo>>(callSiteChain);
        
        ordered.Sort((a, b) => a.Value.Order.CompareTo(b.Value.Order));

        foreach (KeyValuePair<Type, ChainItemInfo> pair in ordered)
        {
            Type serviceType = pair.Key;
            Type implementationType = pair.Value.ImplementationType;
            if (implementationType == null || serviceType == implementationType)
            {
                builder.Append(TypeNameHelper.GetTypeDisplayName(serviceType));
            }
            else
            {
                builder.AppendFormat("{0}({1})",
                    TypeNameHelper.GetTypeDisplayName(serviceType),
                    TypeNameHelper.GetTypeDisplayName(implementationType));
            }

            builder.Append(" -> ");
        }

        builder.Append(TypeNameHelper.GetTypeDisplayName(currentlyResolving));
    }

    private readonly struct ChainItemInfo
    {
        public int Order { get; }
        public Type ImplementationType { get; }

        public ChainItemInfo(int order, Type implementationType)
        {
            Order = order;
            ImplementationType = implementationType;
        }
    }
}
