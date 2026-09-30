using System;
using System.Threading.Tasks;

using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>The MAUI application: one window hosting the Studio shell.</summary>
internal sealed class StudioApplication : Application
{
    private readonly StudioState _state = new();

    public StudioApplication()
    {
        UserAppTheme = AppTheme.Light;
    }

    public StudioState State => _state;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new StudioShell(_state))
        {
            Title = "Cohesion Database Studio (throw-away)",
            Width = 1500,
            Height = 950,
        };

        window.Destroying += async (_, _) =>
        {
            try
            {
                await _state.DisposeAsync();
            }
            catch (Exception exception)
            {
                // Shutdown is best effort; record why an engine refused to close.
                CrashLog.Write("Window.Destroying", exception);
            }
        };

        _ = StartAsync();
        return window;
    }

    private async Task StartAsync()
    {
        try
        {
            await Task.Run(() => _state.ApplyAsync(new StudioSettings()));
        }
        catch (Exception exception)
        {
            // Surface startup failures in the workspace log instead of crashing.
            _state.Log($"Startup failed: {ErrorText.Describe(exception)}");
            CrashLog.Write("Startup", exception);
        }
    }
}
