using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Internal;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

public class StoredSessionCompatibilityTests
{
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(20);

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - InvokeAsync: A custom stored session without IsModified still commits every request")]
    public async Task InvokeAsync_CustomStoredSessionWithoutModificationOverride_CommitsEveryRequest()
    {
        // Arrange
        RecordingSessionStore store = new();
        SessionMiddleware middleware = new(store, new HttpSessionOptions());
        int writesBefore = store.SetCount;

        // Act
        for (int request = 0; request < 2; request++)
        {
            await using SessionTestContext context = new();
            IHttpStoredSession replacement = new LegacyStoredSession(store, "custom-session");
            await middleware.InvokeAsync(context, exchange =>
            {
                // Replacements are the existing seam for a custom stored-session implementation.
                // No mutation occurs, and the class deliberately does not implement IsModified.
                exchange.Session = replacement;
                replacement.IsModified.ShouldBeTrue();
                return Task.CompletedTask;
            });
        }

        // Assert
        (store.SetCount - writesBefore).ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - InvokeAsync: An unchanged built-in session skips the payload write and retains idle refresh")]
    public async Task InvokeAsync_UnchangedBuiltInSession_SkipsPayloadWriteAndRefreshesIdleWindow()
    {
        // Arrange
        RecordingSessionStore store = new();
        await store.SetAsync("existing-session", HttpSessionSerializer.Serialize(new Dictionary<string, byte[]>()), _idleTimeout, CancellationToken.None);
        await using SessionTestContext context = new(requestCookieHeader: ".Cohesion.Session=existing-session");
        SessionMiddleware middleware = new(store, new HttpSessionOptions());
        int writesBefore = store.SetCount;
        int refreshesBefore = store.RefreshCount;
        IHttpStoredSession? observed = null;

        // Act
        await middleware.InvokeAsync(context, async exchange =>
        {
            observed = (IHttpStoredSession)await exchange.LoadSessionAsync(CancellationToken.None);
        });

        // Assert — payload skipping and refresh are existing behavior; IsModified exposes it.
        observed.ShouldNotBeNull();
        observed.IsModified.ShouldBeFalse();
        (store.SetCount - writesBefore).ShouldBe(0);
        (store.RefreshCount - refreshesBefore).ShouldBe(1);
    }

    private sealed class LegacyStoredSession : IHttpStoredSession
    {
        private readonly IHttpSessionStore _store;
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public LegacyStoredSession(IHttpSessionStore store, string id)
        {
            _store = store;
            Id = id;
        }

        public bool IsAvailable => true;

        public string Id { get; private set; }

        public IEnumerable<string> Keys => _values.Keys;

        public Task LoadAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken = default)
            => _store.SetAsync(Id, HttpSessionSerializer.Serialize(_values), _idleTimeout, cancellationToken).AsTask();

        public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value)
            => _values.TryGetValue(key, out value);

        public void Set(string key, byte[] value) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);

        public void Clear() => _values.Clear();

        public void ReassignId(string newId) => Id = newId;
    }
}
