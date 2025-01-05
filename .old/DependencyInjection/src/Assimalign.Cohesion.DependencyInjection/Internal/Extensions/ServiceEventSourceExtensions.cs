using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq.Expressions;
using System.Text;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal static class ServiceEventSourceExtensions
{
    // This is an extension method because this assembly is trimmed at a "type granular" level in Blazor,
    // and the whole DependencyInjectionEventSource type can't be trimmed. So extracting this to a separate
    // type allows for the System.Linq.Expressions usage to be trimmed by the ILLinker.
    public static void ExpressionTreeGenerated(this ServiceEventSource source, ServiceProvider provider, Type serviceType, Expression expression)
    {
        if (source.IsEnabled(EventLevel.Verbose, EventKeywords.All))
        {
            var visitor = new NodeCountingVisitor();
            visitor.Visit(expression);
            source.ExpressionTreeGenerated(serviceType.ToString(), visitor.NodeCount, provider.GetHashCode());
        }
    }

    private sealed class NodeCountingVisitor : ExpressionVisitor
    {
        public int NodeCount { get; private set; }

        public override Expression Visit(Expression e)
        {
            base.Visit(e);
            NodeCount++;
            return e;
        }
    }
}
