using System;
using System.Text;
using System.Collections.Generic;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class CallSiteJsonFormatterVisitor : CallSiteVisitor<CallSiteJsonFormatterVisitor.CallSiteFormatterContext, object>
{
    internal static CallSiteJsonFormatterVisitor Instance = new CallSiteJsonFormatterVisitor();

    private CallSiteJsonFormatterVisitor()
    {
    }

    public string Format(CallSiteService callSite)
    {
        var stringBuilder = new StringBuilder();
        var context = new CallSiteFormatterContext(stringBuilder, 0, new HashSet<CallSiteService>());

        VisitCallSite(callSite, context);

        return stringBuilder.ToString();
    }

    protected override object VisitConstructor(ConstructorCallSite constructorCallSite, CallSiteFormatterContext argument)
    {
        argument.WriteProperty("implementationType", constructorCallSite.ImplementationType);

        if (constructorCallSite.ParameterCallSites.Length > 0)
        {
            argument.StartProperty("arguments");

            CallSiteFormatterContext childContext = argument.StartArray();
            foreach (CallSiteService parameter in constructorCallSite.ParameterCallSites)
            {
                childContext.StartArrayItem();
                VisitCallSite(parameter, childContext);
            }
            argument.EndArray();
        }

        return null;
    }

    protected override object VisitCallSiteMain(CallSiteService callSite, CallSiteFormatterContext argument)
    {
        if (argument.ShouldFormat(callSite))
        {
            CallSiteFormatterContext childContext = argument.StartObject();

            childContext.WriteProperty("serviceType", callSite.ServiceType);
            childContext.WriteProperty("kind", callSite.Kind);
            childContext.WriteProperty("cache", callSite.Cache.Location);

            base.VisitCallSiteMain(callSite, childContext);

            argument.EndObject();
        }
        else
        {
            CallSiteFormatterContext childContext = argument.StartObject();
            childContext.WriteProperty("ref", callSite.ServiceType);
            argument.EndObject();
        }

        return null;
    }

    protected override object VisitConstant(ConstantCallSite constantCallSite, CallSiteFormatterContext argument)
    {
        argument.WriteProperty("value", constantCallSite.DefaultValue ?? "");

        return null;
    }

    protected override object VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, CallSiteFormatterContext argument)
    {
        return null;
    }

    protected override object VisitEnumerable(EnumerableCallSite enumerableCallSite, CallSiteFormatterContext argument)
    {
        argument.WriteProperty("itemType", enumerableCallSite.ItemType);
        argument.WriteProperty("size", enumerableCallSite.ServiceCallSites.Length);

        if (enumerableCallSite.ServiceCallSites.Length > 0)
        {
            argument.StartProperty("items");

            CallSiteFormatterContext childContext = argument.StartArray();
            foreach (CallSiteService item in enumerableCallSite.ServiceCallSites)
            {
                childContext.StartArrayItem();
                VisitCallSite(item, childContext);
            }
            argument.EndArray();
        }
        return null;
    }

    protected override object VisitFactory(FactoryCallSite factoryCallSite, CallSiteFormatterContext argument)
    {
        argument.WriteProperty("method", factoryCallSite.Factory.Method);

        return null;
    }

    internal struct CallSiteFormatterContext
    {
        private readonly HashSet<CallSiteService> _processedCallSites;

        public CallSiteFormatterContext(StringBuilder builder, int offset, HashSet<CallSiteService> processedCallSites)
        {
            Builder = builder;
            Offset = offset;
            _processedCallSites = processedCallSites;
            _firstItem = true;
        }

        private bool _firstItem;

        public int Offset { get; }
        public StringBuilder Builder { get; }

        public bool ShouldFormat(CallSiteService serviceCallSite)
        {
            return _processedCallSites.Add(serviceCallSite);
        }

        public CallSiteFormatterContext IncrementOffset()
        {
            return new CallSiteFormatterContext(Builder, Offset + 4, _processedCallSites)
            {
                _firstItem = true
            };
        }

        public CallSiteFormatterContext StartObject()
        {
            Builder.Append('{');
            return IncrementOffset();
        }

        public void EndObject()
        {
            Builder.Append('}');
        }

        public void StartProperty(string name)
        {
            if (!_firstItem)
            {
                Builder.Append(',');
            }
            else
            {
                _firstItem = false;
            }
            Builder.AppendFormat("\"{0}\":", name);
        }

        public void StartArrayItem()
        {
            if (!_firstItem)
            {
                Builder.Append(',');
            }
            else
            {
                _firstItem = false;
            }
        }

        public void WriteProperty(string name, object value)
        {
            StartProperty(name);
            if (value != null)
            {
                Builder.AppendFormat(" \"{0}\"", value);
            }
            else
            {
                Builder.Append("null");
            }
        }

        public CallSiteFormatterContext StartArray()
        {
            Builder.Append('[');
            return IncrementOffset();
        }

        public void EndArray()
        {
            Builder.Append(']');
        }
    }
}
