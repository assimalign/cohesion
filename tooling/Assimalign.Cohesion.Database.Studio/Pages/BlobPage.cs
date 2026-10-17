using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Blob browser: containers, blob listing, upload (file picker), download, delete, properties.</summary>
internal sealed class BlobPage : ContentPage
{
    private readonly StudioState _state;
    private readonly DatabaseBar _databaseBar;
    private readonly TransactionBar _transactionBar;
    private readonly ObservableCollection<string> _containers = [];
    private readonly ObservableCollection<BlobItem> _blobs = [];
    private readonly CollectionView _containerList;
    private readonly CollectionView _blobList;
    private readonly Entry _newContainer = Ui.Entry("new container", 140);
    private readonly Entry _externalContainer = Ui.Entry("container name", 150);
    private readonly View _managedContainers;
    private readonly View _externalContainers;
    private readonly Label _currentContainer = Ui.Text("(no container)", 13, bold: true);
    private readonly Entry _prefix = Ui.Entry("prefix filter", 160);
    private readonly Entry _uploadName = Ui.Entry("blob name (default: file name)", 220);
    private readonly Entry _contentType = Ui.Entry("content type (default: by extension)", 220);
    private readonly CheckBox _overwrite;
    private readonly Entry _saveFolder = Ui.Entry("download folder", 320, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
    private readonly Editor _properties = Ui.ReadOnlyText();
    private readonly MessageList _messages = new();
    private BlobWorkspace? _workspace;
    private string? _container;
    private BlobItem? _selected;

    public BlobPage(StudioState state)
    {
        _state = state;
        _databaseBar = new DatabaseBar(ConfirmAsync, _messages.Error);
        _databaseBar.DatabaseChanged += async (_, _) =>
        {
            _transactionBar!.Refresh();
            SetContainer(null);
            await RefreshContainersAsync();
        };
        _transactionBar = new TransactionBar(text => _messages.Add(text, MessageKind.Success), _messages.Error);

        _containerList = new CollectionView
        {
            ItemsSource = _containers,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontFamily = Ui.Mono, FontSize = 12, Padding = new Thickness(4, 3) };
                label.BindingContextChanged += (_, _) => label.Text = label.BindingContext as string;
                return label;
            }),
        };
        _containerList.SelectionChanged += async (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is string name)
            {
                SetContainer(name);
                await Guard(ListBlobsAsync);
            }
        };

        _blobList = new CollectionView
        {
            ItemsSource = _blobs,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontFamily = Ui.Mono, FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1, Padding = new Thickness(4, 3) };
                label.BindingContextChanged += (_, _) =>
                {
                    if (label.BindingContext is BlobItem item)
                    {
                        label.Text = item.Summary;
                    }
                };
                return label;
            }),
        };
        _blobList.SelectionChanged += (_, args) =>
        {
            _selected = args.CurrentSelection.FirstOrDefault() as BlobItem;
            _properties.Text = _selected?.Details ?? string.Empty;
        };

        _managedContainers = new VerticalStackLayout
        {
            Spacing = 3,
            Children =
            {
                Ui.Bar(_newContainer, Ui.Button("Create", CreateContainerAsync, _messages.Error)),
                Ui.Bar(Ui.Button("Delete selected", DropContainerAsync, _messages.Error), Ui.Button("Refresh", RefreshContainersAsync, _messages.Error)),
            },
        };
        _externalContainers = new VerticalStackLayout
        {
            Spacing = 3,
            Children =
            {
                Ui.Text("The blob wire has no container verbs: type the name.", 11, color: Ui.Muted),
                Ui.Bar(_externalContainer, Ui.Button("Use", async () =>
                {
                    SetContainer(string.IsNullOrWhiteSpace(_externalContainer.Text) ? null : _externalContainer.Text.Trim());
                    await ListBlobsAsync();
                }, _messages.Error)),
            },
        };

        View containers = Ui.Panel(Ui.Rows(
            (GridLength.Auto, Ui.Heading("Containers")),
            (GridLength.Star, new Border { Content = _containerList, Stroke = Ui.Border, StrokeThickness = 1 }),
            (GridLength.Auto, new Grid { Children = { _managedContainers, _externalContainers } })));

        View blobs = Ui.Rows(
            (GridLength.Auto, Ui.Bar(Ui.Heading("Blobs in"), _currentContainer, _prefix, Ui.Button("List", ListBlobsAsync, _messages.Error))),
            (new GridLength(3, GridUnitType.Star), new Border { Content = _blobList, Stroke = Ui.Border, StrokeThickness = 1 }),
            (GridLength.Auto, Ui.Bar(
                Ui.Button("Upload file...", UploadAsync, _messages.Error),
                _uploadName,
                _contentType,
                Ui.CheckBox("overwrite", true, out _overwrite))),
            (GridLength.Auto, Ui.Bar(
                Ui.Button("Download", DownloadAsync, _messages.Error),
                Ui.Button("Delete", DeleteAsync, _messages.Error),
                Ui.Button("Properties", PropertiesAsync, _messages.Error),
                _saveFolder,
                Ui.Button("Open folder", () =>
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_saveFolder.Text}\"") { UseShellExecute = true });
                    return Task.CompletedTask;
                }, _messages.Error))),
            (new GridLength(2, GridUnitType.Star), _messages));

        View properties = Ui.Panel(Ui.Rows(
            (GridLength.Auto, Ui.Heading("Properties / metadata")),
            (GridLength.Star, _properties)));

        var root = new Grid
        {
            Padding = new Thickness(8),
            RowSpacing = 4,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
        };
        root.Add(_databaseBar, 0, 0);
        root.Add(_transactionBar, 0, 1);
        root.Add(Ui.Columns((new GridLength(260), containers), (GridLength.Star, blobs), (new GridLength(360), properties)), 0, 2);
        Content = root;

        _state.WorkspacesChanged += (_, _) => Ui.OnMain(() => _ = BindAsync());
        _ = BindAsync();
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            // Event handlers report into Messages instead of throwing.
            _messages.Error(exception);
        }
    }

    private Task<bool> ConfirmAsync(string message) => DisplayAlertAsync("Confirm", message, "Yes", "No");

    private async Task BindAsync()
    {
        try
        {
            _workspace = _state.Get<BlobWorkspace>(StudioModel.Blob);
            _transactionBar.Bind(_workspace);
            _containers.Clear();
            _blobs.Clear();
            SetContainer(null);
            bool managed = _workspace?.CanManageContainers ?? true;
            _managedContainers.IsVisible = managed;
            _externalContainers.IsVisible = !managed;
            await _databaseBar.BindAsync(_workspace, _state.Engines is null ? "Engines are starting (see the Workspace page)..." : $"Blob: {_state.GetStatus(StudioModel.Blob)}");
        }
        catch (Exception exception)
        {
            // Binding reports its failure in Messages.
            _messages.Error(exception);
        }
    }

    private BlobWorkspace Workspace()
    {
        BlobWorkspace workspace = _workspace ?? throw new InvalidOperationException("The Blob workspace is not ready (see the Workspace page).");
        return workspace.CurrentDatabase is null ? throw new InvalidOperationException("Select or create a database first.") : workspace;
    }

    private string Container() => _container ?? throw new InvalidOperationException("Select a container first.");

    private BlobItem Selected() => _selected ?? throw new InvalidOperationException("Select a blob first.");

    private void SetContainer(string? name)
    {
        _container = name;
        _currentContainer.Text = name ?? "(no container)";
        _blobs.Clear();
        _selected = null;
        _properties.Text = string.Empty;
    }

    private async Task RefreshContainersAsync()
    {
        _containers.Clear();
        if (_workspace is not { CanManageContainers: true, CurrentDatabase: not null } workspace)
        {
            return;
        }

        List<string> names = await Task.Run(() => workspace.ListContainersAsync());
        foreach (string name in names)
        {
            _containers.Add(name);
        }

        _messages.Add($"{names.Count} container(s) in '{workspace.CurrentDatabase}'.", MessageKind.Detail);
    }

    private async Task CreateContainerAsync()
    {
        BlobWorkspace workspace = Workspace();
        string name = string.IsNullOrWhiteSpace(_newContainer.Text) ? throw new InvalidOperationException("Enter a container name.") : _newContainer.Text.Trim();
        await Task.Run(() => workspace.CreateContainerAsync(name));
        _newContainer.Text = string.Empty;
        _messages.Add($"Container '{name}' created.", MessageKind.Success);
        await RefreshContainersAsync();
    }

    private async Task DropContainerAsync()
    {
        BlobWorkspace workspace = Workspace();
        string name = Container();
        if (!await ConfirmAsync($"Delete container '{name}' and its blobs?"))
        {
            return;
        }

        await Task.Run(() => workspace.DropContainerAsync(name));
        _messages.Add($"Container '{name}' dropped.", MessageKind.Success);
        SetContainer(null);
        await RefreshContainersAsync();
    }

    private async Task ListBlobsAsync()
    {
        BlobWorkspace workspace = Workspace();
        string container = Container();
        string? prefix = _prefix.Text;
        List<BlobItem> items = await Task.Run(() => workspace.ListBlobsAsync(container, prefix));
        _blobs.Clear();
        foreach (BlobItem item in items)
        {
            _blobs.Add(item);
        }

        if (workspace.CanManageContainers)
        {
            try
            {
                IReadOnlyDictionary<string, object?> ownership = await Task.Run(() => workspace.GetContainerOwnershipAsync(container));
                _properties.Text = $"Container '{container}' ownership:\n{string.Join("\n", ownership.Select(pair => $"  {pair.Key}: {ValueFormatter.Format(pair.Value)}"))}";
            }
            catch (Exception exception)
            {
                // Ownership is informational; its absence is not a listing failure.
                _properties.Text = $"Container ownership unavailable: {exception.Message}";
            }
        }

        _messages.Add($"{items.Count} blob(s) in '{container}'{(string.IsNullOrEmpty(prefix) ? string.Empty : $" with prefix '{prefix}'")}.", MessageKind.Success);
    }

    private async Task UploadAsync()
    {
        BlobWorkspace workspace = Workspace();
        string container = Container();
        FileResult? file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Upload to blob" });
        if (file is null)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(_uploadName.Text) ? file.FileName : _uploadName.Text.Trim();
        string contentType = string.IsNullOrWhiteSpace(_contentType.Text) ? GuessContentType(file.FileName) : _contentType.Text.Trim();
        bool overwrite = _overwrite.IsChecked;
        string path = file.FullPath;
        long written = await Task.Run(async () =>
        {
            await using FileStream stream = File.OpenRead(path);
            return await workspace.UploadAsync(container, name, stream, contentType, overwrite);
        });

        _messages.Add($"Uploaded {path} -> {container}/{name} ({contentType}, {written} bytes).", MessageKind.Success);
        await ListBlobsAsync();
    }

    private async Task DownloadAsync()
    {
        BlobWorkspace workspace = Workspace();
        string container = Container();
        BlobItem blob = Selected();
        string folder = string.IsNullOrWhiteSpace(_saveFolder.Text) ? throw new InvalidOperationException("Enter a download folder.") : _saveFolder.Text.Trim();
        Directory.CreateDirectory(folder);
        string target = UniquePath(folder, blob.Name);
        long copied = await Task.Run(async () =>
        {
            await using FileStream stream = File.Create(target);
            return await workspace.DownloadAsync(container, blob.Name, stream);
        });

        _messages.Add($"Downloaded {container}/{blob.Name} -> {target} ({copied} bytes).", MessageKind.Success);
    }

    private async Task DeleteAsync()
    {
        BlobWorkspace workspace = Workspace();
        string container = Container();
        BlobItem blob = Selected();
        if (!await ConfirmAsync($"Delete blob '{blob.Name}'?"))
        {
            return;
        }

        bool deleted = await Task.Run(() => workspace.DeleteAsync(container, blob.Name));
        _messages.Add(deleted ? $"Deleted {container}/{blob.Name}." : $"{container}/{blob.Name} did not exist.", deleted ? MessageKind.Success : MessageKind.Warning);
        await ListBlobsAsync();
    }

    private async Task PropertiesAsync()
    {
        BlobWorkspace workspace = Workspace();
        string container = Container();
        BlobItem blob = Selected();
        BlobItem? current = await Task.Run(() => workspace.GetPropertiesAsync(container, blob.Name));
        _properties.Text = current?.Details ?? $"{blob.Name}: not found";
        _messages.Add($"Properties of {container}/{blob.Name} refreshed.", MessageKind.Detail);
    }

    private static string UniquePath(string folder, string blobName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. blobName.Select(c => c is '/' or '\\' || invalid.Contains(c) ? '_' : c)]);
        string path = Path.Combine(folder, safe);
        for (int i = 1; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}");
        }

        return path;
    }

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
