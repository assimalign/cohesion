using System;


namespace Assimalign.Cohesion;

/// <summary>
/// This class contains the core environment variables used for 
/// </summary>
public static class AppEnvironment
{
    private static readonly string EnvironmentKey = "COHESION_ENVIRONMENT";





    public static string? GetEnvironmentName()
    {
        return Environment.GetEnvironmentVariable(EnvironmentKey, EnvironmentVariableTarget.Process);
    }
}