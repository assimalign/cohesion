using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Builds the MAUI application; the UI is code-only (no MAUI XAML).</summary>
internal static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<StudioApplication>();
        return builder.Build();
    }
}
