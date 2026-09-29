using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A declared resource command as handed to an <see cref="IResourceCommandInputResolver"/>
/// before delivery.
/// </summary>
/// <param name="Id">The command's deterministic identity (<see cref="IResourceCommand.Id"/>).</param>
/// <param name="Kind">The wire command kind (<see cref="IResourceCommand.Kind"/>).</param>
/// <param name="Owner">The ownership identity of the declaring application (<see cref="IResourceCommand.Owner"/>).</param>
/// <param name="Key">The provider conflict key (<see cref="IResourceCommand.Key"/>).</param>
/// <param name="Payload">The declared canonical UTF-8 JSON payload (<see cref="IResourceCommand.Payload"/>).</param>
public sealed record ResourceCommandInput(
    string Id,
    string Kind,
    string Owner,
    string Key,
    ReadOnlyMemory<byte> Payload);
