using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Builds the in-process infrastructure a gateway runs its resources on: its options and the program
/// bound to each resource.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InProcessApplicationGatewayBuilderExtensions.UseInProcess(ApplicationGatewayBuilder, Action{InProcessInfrastructureBuilder}?)"/>
/// configures one when the gateway is built, and the gateway owns the infrastructure it builds.
/// </para>
/// <para>
/// A gateway program's build binds the program of every resource project it references with
/// <c>CohesionResource</c> metadata, so most gateways bind nothing here. <see cref="AddProgram"/>
/// binds one explicitly, and an explicit binding takes the place of the build's for the same
/// resource.
/// </para>
/// </remarks>
[DebuggerDisplay("Programs = {_programs.Count}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class InProcessInfrastructureBuilder
{
    private readonly Dictionary<(ProviderName Provider, ResourceName Name), InProcessProgram> _programs = [];

    /// <summary>
    /// Creates a builder with the default options and no programs of its own.
    /// </summary>
    public InProcessInfrastructureBuilder()
    {
    }

    /// <summary>
    /// Gets the options the infrastructure is built with.
    /// </summary>
    public InProcessInfrastructureOptions Options { get; } = new();

    /// <summary>
    /// Binds a resource to the program that runs it: the entry point the gateway program calls for
    /// it, in place of the one the gateway program's build bound.
    /// </summary>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="main">The program's entry point.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>, or the builder already binds the resource.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="main"/> is <see langword="null"/>.</exception>
    public InProcessInfrastructureBuilder AddProgram(ProviderName provider, ResourceName name, Func<string[], Task> main)
    {
        InProcessProgram program = InProcessProgram.Create(provider, name, main);

        if (!_programs.TryAdd((provider, name), program))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ProgramDuplicate, provider, name), nameof(name));
        }

        return this;
    }

    /// <summary>
    /// Builds the infrastructure: the programs bound here, then every program the gateway program's
    /// build bound to a resource not bound here.
    /// </summary>
    /// <remarks>
    /// Each call builds new infrastructure at a new endpoint, which its caller owns.
    /// </remarks>
    /// <returns>The infrastructure.</returns>
    public InProcessInfrastructure Build()
    {
        var infrastructure = new InProcessInfrastructure
        {
            StopTimeout = Options.StopTimeout,
        };

        foreach (InProcessProgram program in _programs.Values)
        {
            infrastructure.AddProgram(program.Provider, program.Name, program.Main);
        }

        foreach (InProcessProgram program in InProcessProgramCatalog.GetPrograms())
        {
            if (!_programs.ContainsKey((program.Provider, program.Name)))
            {
                infrastructure.AddProgram(program.Provider, program.Name, program.Main);
            }
        }

        return infrastructure;
    }

    private sealed class DebugView
    {
        private readonly InProcessInfrastructureBuilder _builder;

        public DebugView(InProcessInfrastructureBuilder builder)
        {
            _builder = builder;
        }

        public InProcessInfrastructureOptions Options => _builder.Options;
    }
}
