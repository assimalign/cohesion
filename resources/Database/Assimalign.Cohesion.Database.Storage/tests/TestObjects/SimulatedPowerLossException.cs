using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// The failure every write raises once a <see cref="CrashPoint"/> lost power: the process the
/// storage runs in is gone, and nothing it does afterwards reaches the media.
/// </summary>
public sealed class SimulatedPowerLossException : IOException
{
    /// <summary>
    /// Initializes a new <see cref="SimulatedPowerLossException"/>.
    /// </summary>
    public SimulatedPowerLossException()
        : base("Simulated power loss.")
    {
    }

    /// <summary>
    /// Asserts that <paramref name="action"/> ends in the simulated power loss, and returns it:
    /// thrown as itself, or as the cause of the <see cref="StorageOfflineException"/> its storage
    /// goes offline with, whether a durable flush (#1243), a drain of the journal's append buffer
    /// (#1252) or a header slot write (#1268) lost power. Either way the process is gone; the test
    /// reopens what the media holds.
    /// </summary>
    /// <param name="action">The operation that loses power.</param>
    /// <param name="context">What the test was doing, for the assertion message.</param>
    /// <returns>The power loss.</returns>
    /// <exception cref="Shouldly.ShouldAssertException">The operation completed; any other failure propagates.</exception>
    public static SimulatedPowerLossException ShouldBeThrownBy(Action action, string? context = null)
    {
        try
        {
            action();
        }
        catch (SimulatedPowerLossException loss)
        {
            return loss;
        }
        catch (StorageOfflineException offline) when (offline.InnerException is SimulatedPowerLossException loss)
        {
            return loss;
        }

        throw new Shouldly.ShouldAssertException($"Expected a simulated power loss{(context is null ? "" : $" ({context})")}, but the operation completed.");
    }
}
