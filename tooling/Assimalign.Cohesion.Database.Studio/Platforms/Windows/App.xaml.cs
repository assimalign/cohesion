using System;

using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace Assimalign.Cohesion.Database.Studio.WinUI;

/// <summary>The WinUI head of the MAUI application.</summary>
public partial class App : MauiWinUIApplication
{
    /// <summary>Initializes the WinUI application and routes unhandled UI exceptions to the crash log.</summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            // Keep the tool alive: log the failure and mark it handled.
            CrashLog.Write("WinUI.UnhandledException", args.Exception);
            args.Handled = true;
        };
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
