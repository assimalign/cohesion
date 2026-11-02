using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// The programs a gateway program's build bound to its resources: the entry points of the resource
/// projects it references with <c>CohesionResource</c> metadata.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion source generator fills it: in a gateway program that references this package, it
/// emits a module initializer that registers each referenced resource's program before the gateway
/// program's own code runs. Application code does not call it.
/// </para>
/// <para>
/// <see cref="InProcessInfrastructureBuilder.Build"/> binds every program here that the builder did
/// not bind itself, so a bare <c>UseInProcess()</c> runs every resource the gateway program
/// references.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class InProcessProgramCatalog
{
    private static readonly ConcurrentDictionary<(ProviderName Provider, ResourceName Name), InProcessProgram> _programs = new();

    /// <summary>
    /// Registers the program that runs a resource.
    /// </summary>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="main">The program's entry point.</param>
    /// <exception cref="ArgumentException"><paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>, or the resource already has a program.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="main"/> is <see langword="null"/>.</exception>
    public static void Register(ProviderName provider, ResourceName name, Func<string[], Task> main)
    {
        InProcessProgram program = InProcessProgram.Create(provider, name, main);

        if (!_programs.TryAdd((provider, name), program))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ProgramDuplicate, provider, name), nameof(name));
        }
    }

    // A snapshot, in a stable order, so a build binds the same programs in the same order each time.
    internal static InProcessProgram[] GetPrograms()
    {
        return [.. _programs.Values
            .OrderBy(static program => program.Provider.Value, StringComparer.Ordinal)
            .ThenBy(static program => program.Name.Value, StringComparer.Ordinal)];
    }
}
