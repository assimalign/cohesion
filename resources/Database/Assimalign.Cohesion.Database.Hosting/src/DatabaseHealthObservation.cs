using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>An immutable, transport-neutral observation from the application's health evaluation.</summary>
public sealed class DatabaseHealthObservation
{
    internal DatabaseHealthObservation(string name, string engine, string database)
    {
        Name = name;
        Status = DatabaseHealthStatus.Healthy;
        Description = "The database is online.";
        Data = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine"] = engine,
            ["database"] = database,
        });
    }

    internal DatabaseHealthObservation(string name, HealthCheckResult result)
    {
        Name = name;
        Status = result.Status switch
        {
            HealthStatus.Healthy => DatabaseHealthStatus.Healthy,
            HealthStatus.Degraded => DatabaseHealthStatus.Degraded,
            _ => DatabaseHealthStatus.Unhealthy,
        };
        Description = result.Description;
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.Data is not null)
        {
            foreach (var entry in result.Data)
            {
                data.Add(entry.Key, Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }
        Data = new ReadOnlyDictionary<string, string>(data);
    }

    /// <summary>Gets the stable component name.</summary>
    /// <remarks>
    /// The public forms are <c>engine/&lt;escaped engine&gt;</c>,
    /// <c>database/&lt;escaped engine&gt;/&lt;escaped lowercase database&gt;</c>, and
    /// <c>database.accepting</c>. Each segment is URI-escaped separately. Engine identity is ordinal;
    /// database names use the area's invariant lower-case storage-name fold.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the observed component status.</summary>
    public DatabaseHealthStatus Status { get; }

    /// <summary>Gets the observation's description, with exception types only.</summary>
    public string? Description { get; }

    /// <summary>Gets immutable diagnostic values formatted with the invariant culture.</summary>
    public IReadOnlyDictionary<string, string> Data { get; }
}
