using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Management.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>Exposes versioned CORS policy state through an explicitly advertised runtime contract.</summary>
[DebuggerDisplay("CorsResourceStateHandler")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class CorsResourceStateHandler : IResourceStateHandler
{
    private readonly CorsPolicyStore _store;
    private readonly ResourceStateDefinition _definition;
    private readonly ResourceStateDeclaration _initialState;

    private CorsResourceStateHandler(CorsPolicyStore store, ResourceStateDefinition definition)
    {
        _store = store;
        _definition = definition;
        _initialState = new ResourceStateDeclaration(definition.Name, "configuration", new ResourceStateValue(true,
            JsonSerializer.SerializeToElement(store.GetConfiguration(), CorsJsonContext.Default.CorsConfiguration)));
    }

    /// <inheritdoc />
    public ResourceStateDefinition Definition => _definition;

    /// <inheritdoc />
    public IReadOnlyList<ResourceStateDeclaration> InitialState => [_initialState];

    /// <summary>Creates a runtime-discovered adapter and captures the store's effective startup configuration.</summary>
    /// <param name="store">The policy store used by the pipeline after configuration is applied.</param>
    /// <returns>The resource state handler.</returns>
    /// <exception cref="ArgumentNullException">The store is null.</exception>
    public static CorsResourceStateHandler Create(CorsPolicyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new CorsResourceStateHandler(store, BuiltInStateDefinitions.Load("web.cors"));
    }

    /// <inheritdoc />
    public ValueTask<ResourceStateValidation> ValidateAsync(ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            CorsConfiguration? configuration = JsonSerializer.Deserialize(desired.Value, CorsJsonContext.Default.CorsConfiguration);
            if (!desired.Exists || key.Value != "configuration" || configuration is null) { return ValueTask.FromResult(new ResourceStateValidation(false, "cors-invalid")); }
            _ = CorsPolicyStore.Create(configuration);
            if (_store.GetConfiguration().Policies.Keys.Any(name => !configuration.Policies.ContainsKey(name)))
            {
                return ValueTask.FromResult(new ResourceStateValidation(false, "cors-policy-retention"));
            }
            return ValueTask.FromResult(new ResourceStateValidation(true));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return ValueTask.FromResult(new ResourceStateValidation(false, "cors-invalid"));
        }
    }

    /// <inheritdoc />
    public ValueTask<ResourceStateObservation> ReadAsync(ResourceStateKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CorsPolicyState state = _store.ReadState();
        JsonElement value = JsonSerializer.SerializeToElement(state.Configuration, CorsJsonContext.Default.CorsConfiguration);
        return ValueTask.FromResult(new ResourceStateObservation(new ResourceStateValue(true, value), state.Revision, state.ValueDigest, Guid.Empty));
    }

    /// <inheritdoc />
    public ValueTask<ResourceStateApplyResult> ApplyAsync(ResourceStateApplyContext context, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CorsConfiguration? configuration = JsonSerializer.Deserialize(desired.Value, CorsJsonContext.Default.CorsConfiguration);
        if (!desired.Exists || key.Value != "configuration" || configuration is null)
        {
            return ValueTask.FromResult(new ResourceStateApplyResult(ResourceStateApplyStatus.Rejected, _store.ReadState().Revision, "cors-invalid"));
        }
        CorsPolicyUpdateStatus status = _store.ApplyVersioned(configuration, context.ResourceIncarnation, context.Revision, context.ValueDigest);
        ResourceStateApplyStatus outcome = status switch
        {
            CorsPolicyUpdateStatus.Applied => ResourceStateApplyStatus.Applied,
            CorsPolicyUpdateStatus.AlreadyCurrent => ResourceStateApplyStatus.AlreadyCurrent,
            CorsPolicyUpdateStatus.Stale => ResourceStateApplyStatus.Stale,
            _ => ResourceStateApplyStatus.Conflict
        };
        return ValueTask.FromResult(new ResourceStateApplyResult(outcome, _store.ReadState().Revision));
    }

    private sealed class DebugView
    {
        private readonly CorsResourceStateHandler _target;
        public DebugView(CorsResourceStateHandler target) { _target = target; }
        public ResourceStateDefinition Definition => _target.Definition;
    }
}
