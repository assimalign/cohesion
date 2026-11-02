using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

/// <summary>
/// The program a resource runs: the entry point the gateway program calls for it.
/// </summary>
internal sealed record InProcessProgram(ProviderName Provider, ResourceName Name, Func<string[], Task> Main);
