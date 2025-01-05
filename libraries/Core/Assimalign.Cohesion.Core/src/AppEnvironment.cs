using System;


namespace Assimalign.Cohesion;

/// <summary>
/// This class contains the core environment variables used for 
/// </summary>
public static partial class AppEnvironment
{
    public static string? GetEnvironmentName()
    {
        return Environment.GetEnvironmentVariable(Keys.EnvironmentKey, EnvironmentVariableTarget.Process);
    }
}