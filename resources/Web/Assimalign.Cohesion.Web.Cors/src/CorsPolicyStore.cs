using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Publishes validated CORS configuration snapshots to a pipeline installed with
/// <see cref="CorsExtensions.UseDynamicCors(IWebApplicationPipelineBuilder, CorsPolicyStore)"/>.
/// </summary>
/// <remarks>
/// Each replacement is atomic. Requests retain the immutable policy they selected, including while
/// their response head is waiting to commit. This store owns no endpoint, transport, or persistence;
/// its configuration lasts for this process and changes only when <see cref="Replace"/> succeeds.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("CorsPolicyStore")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class CorsPolicyStore
{
    private readonly Lock _sync = new();
    private Snapshot _snapshot;

    private CorsPolicyStore(Snapshot snapshot)
    {
        _snapshot = snapshot;
    }

    /// <summary>Creates an isolated store from a declarative CORS configuration.</summary>
    /// <param name="configuration">The initial default and named policies, copied before publication.</param>
    /// <returns>A new store containing the normalized configuration.</returns>
    /// <exception cref="ArgumentNullException">The configuration or one of its collections or policies is null.</exception>
    /// <exception cref="ArgumentException">A policy name, origin, method, header name, or combination of options is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A preflight cache lifetime is negative or exceeds <see cref="TimeSpan.MaxValue"/>.</exception>
    public static CorsPolicyStore Create(CorsConfiguration configuration)
    {
        return new CorsPolicyStore(BuildSnapshot(configuration));
    }

    /// <summary>Reads a detached copy of the currently published, normalized configuration.</summary>
    /// <returns>A configuration whose objects and collections may be changed without affecting this store.</returns>
    public CorsConfiguration GetConfiguration()
    {
        return CopyConfiguration(Volatile.Read(ref _snapshot).Configuration);
    }

    /// <summary>Validates and atomically replaces the complete default and named policy configuration.</summary>
    /// <remarks>
    /// Existing named policies must be retained so endpoints that use those names keep resolving.
    /// New names may be added and become subject to the same retention rule. The default policy may
    /// be removed. Validation failures leave every currently published policy unchanged.
    /// Concurrent replacements serialize publication and are last-writer-wins for retained names.
    /// </remarks>
    /// <param name="configuration">The replacement configuration, copied before publication.</param>
    /// <exception cref="ArgumentNullException">The configuration or one of its collections or policies is null.</exception>
    /// <exception cref="ArgumentException">A policy name, origin, method, header name, or combination of options is invalid, or an existing named policy is omitted.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A preflight cache lifetime is negative or exceeds <see cref="TimeSpan.MaxValue"/>.</exception>
    public void Replace(CorsConfiguration configuration)
    {
        Snapshot replacement = BuildSnapshot(configuration);

        lock (_sync)
        {
            ValidateRetainedPolicies(replacement, configuration);

            Volatile.Write(ref _snapshot, replacement);
        }
    }

    internal (CorsPolicy? DefaultPolicy, FrozenDictionary<string, CorsPolicy> Policies) GetPolicies()
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        return (snapshot.DefaultPolicy, snapshot.Policies);
    }

    private void ValidateRetainedPolicies(Snapshot replacement, CorsConfiguration configuration)
    {
        foreach (string name in _snapshot.Policies.Keys)
        {
            if (!replacement.Policies.ContainsKey(name))
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicCorsPolicyRemovalNotAllowed, name), nameof(configuration));
            }
        }
    }

    private static Snapshot BuildSnapshot(CorsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Policies);

        CorsPolicy? defaultPolicy = configuration.DefaultPolicy is { } configuredDefault
            ? BuildPolicy(configuredDefault) : null;
        Dictionary<string, CorsPolicy> policies = new(StringComparer.Ordinal);
        Dictionary<string, CorsPolicyConfiguration> normalizedPolicies = new(StringComparer.Ordinal);
        foreach ((string name, CorsPolicyConfiguration configuredPolicy) in configuration.Policies)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            CorsPolicy policy = BuildPolicy(configuredPolicy);
            policies.Add(name, policy);
            normalizedPolicies.Add(name, DescribePolicy(policy));
        }

        return new Snapshot
        {
            Configuration = new CorsConfiguration
            {
                DefaultPolicy = defaultPolicy is null ? null : DescribePolicy(defaultPolicy),
                Policies = normalizedPolicies,
            },
            DefaultPolicy = defaultPolicy,
            Policies = policies.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    private static CorsPolicy BuildPolicy(CorsPolicyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Origins);
        ArgumentNullException.ThrowIfNull(configuration.Methods);
        ArgumentNullException.ThrowIfNull(configuration.Headers);
        ArgumentNullException.ThrowIfNull(configuration.ExposedHeaders);

        CorsPolicyBuilder builder = new();
        builder.WithOrigins(configuration.Origins);
        builder.WithMethods(configuration.Methods);
        builder.WithHeaders(CreateHeaders(configuration.Headers));
        builder.WithExposedHeaders(CreateHeaders(configuration.ExposedHeaders));
        if (configuration.AllowAnyOrigin)
        {
            builder.AllowAnyOrigin();
        }
        if (configuration.AllowAnyMethod)
        {
            builder.AllowAnyMethod();
        }
        if (configuration.AllowAnyHeader)
        {
            builder.AllowAnyHeader();
        }
        if (configuration.AllowCredentials)
        {
            builder.AllowCredentials();
        }
        if (configuration.PreflightMaxAgeSeconds is { } seconds)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(seconds);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond);
            builder.SetPreflightMaxAge(TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond));
        }

        try
        {
            return builder.Build();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(ErrorMessages.DynamicCorsPolicyInvalid, nameof(configuration), exception);
        }
    }

    private static HttpFieldName[] CreateHeaders(string[] names)
    {
        HttpFieldName[] headers = new HttpFieldName[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            headers[index] = new HttpFieldName(names[index]);
        }

        return headers;
    }

    private static CorsPolicyConfiguration DescribePolicy(CorsPolicy policy)
    {
        string[] headers = new string[policy.Headers.Count];
        for (int index = 0; index < headers.Length; index++)
        {
            headers[index] = policy.Headers[index].Value;
        }
        string[] exposedHeaders = new string[policy.ExposedHeaders.Count];
        for (int index = 0; index < exposedHeaders.Length; index++)
        {
            exposedHeaders[index] = policy.ExposedHeaders[index].Value;
        }

        return new CorsPolicyConfiguration
        {
            AllowAnyOrigin = policy.AllowsAnyOrigin,
            Origins = [.. policy.Origins],
            AllowAnyMethod = policy.AllowsAnyMethod,
            Methods = [.. policy.Methods],
            AllowAnyHeader = policy.AllowsAnyHeader,
            Headers = headers,
            ExposedHeaders = exposedHeaders,
            AllowCredentials = policy.SupportsCredentials,
            PreflightMaxAgeSeconds = policy.PreflightMaxAge?.Ticks / TimeSpan.TicksPerSecond,
        };
    }

    private static CorsConfiguration CopyConfiguration(CorsConfiguration configuration)
    {
        Dictionary<string, CorsPolicyConfiguration> policies = new(StringComparer.Ordinal);
        foreach ((string name, CorsPolicyConfiguration policy) in configuration.Policies)
        {
            policies.Add(name, CopyPolicy(policy));
        }

        return new CorsConfiguration
        {
            DefaultPolicy = configuration.DefaultPolicy is { } defaultPolicy ? CopyPolicy(defaultPolicy) : null,
            Policies = policies,
        };
    }

    private static CorsPolicyConfiguration CopyPolicy(CorsPolicyConfiguration policy)
    {
        return new CorsPolicyConfiguration
        {
            AllowAnyOrigin = policy.AllowAnyOrigin,
            Origins = [.. policy.Origins],
            AllowAnyMethod = policy.AllowAnyMethod,
            Methods = [.. policy.Methods],
            AllowAnyHeader = policy.AllowAnyHeader,
            Headers = [.. policy.Headers],
            ExposedHeaders = [.. policy.ExposedHeaders],
            AllowCredentials = policy.AllowCredentials,
            PreflightMaxAgeSeconds = policy.PreflightMaxAgeSeconds,
        };
    }

    private sealed class Snapshot
    {
        public required CorsConfiguration Configuration { get; init; }

        public CorsPolicy? DefaultPolicy { get; init; }

        public required FrozenDictionary<string, CorsPolicy> Policies { get; init; }
    }

    private sealed class DebugView
    {
        public DebugView(CorsPolicyStore value)
        {
        }
    }
}
