using System;
using System.Reflection;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting;

/// <summary>
/// Creates scheduler application builders.
/// </summary>
public static class SchedulerApplication
{
    /// <summary>
    /// Creates a builder for a scheduler application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the scheduler application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static ISchedulerApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(SchedulerApplication).Assembly;
        return new SchedulerApplicationBuilder(args, resourceAssembly);
    }

    internal static ISchedulerApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new SchedulerApplicationBuilder(args, resourceAssembly);
    }
}
