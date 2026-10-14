using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal sealed class RecordingCompletionFeature : IWebResponseCompletionFeature
{
    public string Name => nameof(RecordingCompletionFeature);
    internal List<Func<ValueTask>> Callbacks { get; } = [];
    public void Register(Func<ValueTask> callback) => Callbacks.Add(callback);
}
