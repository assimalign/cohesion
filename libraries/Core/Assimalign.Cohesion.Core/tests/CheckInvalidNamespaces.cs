using System.Linq;
using System.Reflection;
using Xunit;

namespace System.Tests;

public class CheckInvalidNamespaces
{
    [Fact(Skip = "Need to figure out best way to load assembly")]
    public void TestInvalidNamespaces()
    {
        string[] disallowNamespaces = 
        [
            "System.SumTypes",
            "Assimalign.Cohesion.System",
            "Assimalign.Cohesion.Core"
        ];

        var assembly = Assembly.LoadFrom("Assimalign.Cohesion.Core.dll");
        var types = assembly.GetTypes();
        var namespaces = types.Select(type => new
        {
            Namespace = type.Namespace,
            TypeName = type.Name

        }).Where( n => n.Namespace is not null) ?? [];

        foreach (var ns in namespaces)
        {
            foreach (var notallowed in disallowNamespaces)
            {
                Assert.True(ns.Namespace!.StartsWith(notallowed) && ns.Namespace.EndsWith(notallowed));
            }
        }
    }
}
