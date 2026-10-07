using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// Data root and per-model connection mode. Apply disposes every engine/server/session and builds
/// them again: one engine per model under &lt;root&gt;\&lt;model&gt;, plus loopback servers where chosen.
/// </summary>
internal sealed class WorkspacePage : ContentPage
{
    private static readonly ConnectionMode[] _modes = [ConnectionMode.Embedded, ConnectionMode.WireLoopback, ConnectionMode.WireExternal];

    private readonly StudioState _state;
    private readonly Entry _dataRoot = Ui.Entry("data root folder", 560, StudioSettings.DefaultDataRoot);
    private readonly Dictionary<StudioModel, (Picker Mode, Entry Port, Entry External, Label Status)> _rows = [];
    private readonly ObservableCollection<string> _log = [];
    private readonly Label _applyStatus = Ui.Text(string.Empty, 12, color: Ui.Muted);
    private readonly CollectionView _logView;

    public WorkspacePage(StudioState state)
    {
        _state = state;

        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 6 };
        foreach (double width in new double[] { 110, 230, 110, 200 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width)));
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headers = ["Model", "Mode", "Loopback port", "External host:port", "Status"];
        for (int i = 0; i < headers.Length; i++)
        {
            grid.Add(Ui.Text(headers[i], bold: true), i, 0);
        }

        int row = 1;
        foreach (StudioModel model in Enum.GetValues<StudioModel>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Picker mode = model.HasWireServer
                ? Ui.Picker([.. _modes.Select(m => m.DisplayName)], 220)
                : Ui.Picker([ConnectionMode.Embedded.DisplayName], 220);
            mode.IsEnabled = model.HasWireServer;
            Entry port = Ui.Entry("0 = auto", 100, "0");
            Entry external = Ui.Entry("host:port", 190, "127.0.0.1:5740");
            Label status = Ui.Text(model.HasWireServer ? "not started" : "Embedded only: Documents has no wire server or client yet", 12, color: Ui.Muted);
            status.LineBreakMode = LineBreakMode.WordWrap;
            mode.SelectedIndexChanged += (_, _) => UpdateRow(model);

            grid.Add(Ui.Text(model.DisplayName, bold: true), 0, row);
            grid.Add(mode, 1, row);
            grid.Add(port, 2, row);
            grid.Add(external, 3, row);
            grid.Add(status, 4, row);
            _rows[model] = (mode, port, external, status);
            UpdateRow(model);
            row++;
        }

        _logView = new CollectionView
        {
            ItemsSource = _log,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontFamily = Ui.Mono, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
                label.BindingContextChanged += (_, _) => label.Text = label.BindingContext as string;
                return label;
            }),
        };

        Button apply = Ui.Button("Apply: (re)start engines and servers", ApplyAsync, exception => _applyStatus.Text = ErrorText.Describe(exception));
        apply.BackgroundColor = Ui.Accent;
        apply.TextColor = Colors.White;

        var help = Ui.Text(
            "Embedded: the page talks to the in-process engine through DatabaseSession.\n" +
            "Wire (loopback): the Studio starts that model's server on 127.0.0.1 (port 0 = OS-assigned) over the SAME engine and talks to it through the real client (Sql/Graph/KeyValuePair/Blob.Client). " +
            "Database create/drop/list and KEYSPACES/containers still go through the engine, because the wire carries no management verbs.\n" +
            "Wire (external): the real client against an already-running server, e.g. the SampleHost fixture (SQL on its Db endpoint). Type the database name on the model page; nothing is listed.\n" +
            "Documents: embedded only (no wire server; Documents.Client/src is empty).\n" +
            "Engines use file storage under <data root>\\<model>; Apply releases the files before re-opening them.",
            12,
            color: Ui.Muted);
        help.LineBreakMode = LineBreakMode.WordWrap;

        var root = new Grid
        {
            Padding = new Thickness(12),
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
        };

        root.Add(Ui.Heading("Workspace"), 0, 0);
        root.Add(Ui.Bar(
            Ui.Text("Data root:", bold: true),
            _dataRoot,
            Ui.Button("Browse...", BrowseAsync, exception => _applyStatus.Text = exception.Message),
            Ui.Button("Open in Explorer", () =>
            {
                Directory.CreateDirectory(_dataRoot.Text ?? StudioSettings.DefaultDataRoot);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataRoot.Text}\"") { UseShellExecute = true });
                return Task.CompletedTask;
            }, exception => _applyStatus.Text = exception.Message),
            Ui.Button("Default", () =>
            {
                _dataRoot.Text = StudioSettings.DefaultDataRoot;
                return Task.CompletedTask;
            })), 0, 1);
        root.Add(Ui.Panel(grid, new Thickness(10)), 0, 2);
        root.Add(Ui.Bar(apply, _applyStatus), 0, 3);
        root.Add(help, 0, 4);
        root.Add(Ui.Heading("Log"), 0, 5);
        root.Add(new Border { Content = _logView, Stroke = Ui.Border, StrokeThickness = 1, BackgroundColor = Colors.White }, 0, 6);
        Content = root;

        foreach (string line in _state.LogLines)
        {
            _log.Add(line);
        }

        _state.LogAdded += (_, line) => Ui.OnMain(() =>
        {
            _log.Add(line);
            _logView.ScrollTo(_log.Count - 1, position: ScrollToPosition.End, animate: false);
        });
        _state.WorkspacesChanged += (_, _) => Ui.OnMain(RefreshStatus);
    }

    private void UpdateRow(StudioModel model)
    {
        if (!_rows.TryGetValue(model, out var row))
        {
            return;
        }

        ConnectionMode mode = SelectedMode(row.Mode);
        row.Port.IsEnabled = mode == ConnectionMode.WireLoopback;
        row.External.IsEnabled = mode == ConnectionMode.WireExternal;
    }

    private static ConnectionMode SelectedMode(Picker picker)
        => picker.SelectedIndex >= 0 && picker.SelectedIndex < _modes.Length ? _modes[picker.SelectedIndex] : ConnectionMode.Embedded;

    private void RefreshStatus()
    {
        foreach (var (model, row) in _rows)
        {
            string status = _state.GetStatus(model);
            row.Status.Text = status;
            row.Status.TextColor = status.StartsWith("FAILED", StringComparison.Ordinal) ? Ui.Bad : Ui.Good;
        }

        _dataRoot.Text = _state.Settings.DataRoot;
    }

    private async Task ApplyAsync()
    {
        var settings = new StudioSettings { DataRoot = string.IsNullOrWhiteSpace(_dataRoot.Text) ? StudioSettings.DefaultDataRoot : _dataRoot.Text.Trim() };
        foreach (var (model, row) in _rows)
        {
            ModelSettings modelSettings = settings.Models[model];
            modelSettings.Mode = SelectedMode(row.Mode);
            modelSettings.LoopbackPort = int.TryParse(row.Port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0;
            modelSettings.ExternalEndpoint = row.External.Text ?? string.Empty;
        }

        _applyStatus.Text = "applying...";
        _applyStatus.TextColor = Ui.Muted;
        await Task.Run(() => _state.ApplyAsync(settings));
        bool failed = Enum.GetValues<StudioModel>().Any(model => _state.GetStatus(model).StartsWith("FAILED", StringComparison.Ordinal));
        _applyStatus.Text = failed ? "applied with failures (see Status/Log)" : $"applied at {DateTime.Now:HH:mm:ss}";
        _applyStatus.TextColor = failed ? Ui.Bad : Ui.Good;
    }

    private async Task BrowseAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
        }

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _dataRoot.Text = folder.Path;
        }
    }
}
