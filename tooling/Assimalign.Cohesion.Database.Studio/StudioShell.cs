using Microsoft.Maui.Controls;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Left navigation: workspace, the three query workspaces, and the two browsers.</summary>
internal sealed class StudioShell : Shell
{
    public StudioShell(StudioState state)
    {
        FlyoutBehavior = Microsoft.Maui.FlyoutBehavior.Locked;
        FlyoutWidth = 170;
        Title = "Cohesion Database Studio";

        Add("Workspace", new WorkspacePage(state));
        Add("SQL", new QueryPage(state, StudioModel.Sql));
        Add("Documents (OQL)", new QueryPage(state, StudioModel.Documents));
        Add("Graph (GQL)", new QueryPage(state, StudioModel.Graph));
        Add("Key-Value", new KeyValuePage(state));
        Add("Blob", new BlobPage(state));
    }

    private void Add(string title, ContentPage page)
    {
        page.Title = title;
        Items.Add(new FlyoutItem
        {
            Title = title,
            Items = { new ShellContent { Title = title, Content = page } },
        });
    }
}
