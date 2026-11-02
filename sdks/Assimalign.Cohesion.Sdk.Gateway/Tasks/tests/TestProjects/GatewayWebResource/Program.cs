using System;

using Assimalign.Cohesion.Web.Hosting;

namespace GatewayWebResource;

/// <summary>The resource program an in-process gateway references.</summary>
public static class Program
{
    /// <summary>Loads a type from the App.Web framework and names its assembly.</summary>
    /// <returns>The Web.Hosting assembly name.</returns>
    public static string Describe()
    {
        return typeof(WebApplication).Assembly.GetName().Name!;
    }

    /// <summary>Runs the resource on its own.</summary>
    public static void Main()
    {
        Console.WriteLine(Describe());
    }
}
