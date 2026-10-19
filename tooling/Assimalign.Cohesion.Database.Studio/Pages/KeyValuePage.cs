using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Key-value browser: get / put (with conditions) / delete / exists, prefix or range scans, KEYSPACES.</summary>
internal sealed class KeyValuePage : ContentPage
{
    private readonly StudioState _state;
    private readonly DatabaseBar _databaseBar;
    private readonly TransactionBar _transactionBar;
    private readonly Entry _key = Ui.Entry("key", 280);
    private readonly Picker _keyEncoding = Ui.Picker(["UTF-8", "Hex"], 90);
    private readonly Editor _value = new() { FontFamily = Ui.Mono, FontSize = 12, HeightRequest = 120, AutoSize = EditorAutoSizeOption.Disabled, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, Placeholder = "value" };
    private readonly Picker _valueEncoding = Ui.Picker(["UTF-8", "Hex"], 90);
    private readonly Picker _condition = Ui.Picker(["Unconditional", "If absent", "If ETag matches"], 150);
    private readonly Entry _etag = Ui.Entry("etag", 120);
    private readonly Entry _prefix = Ui.Entry("prefix", 130);
    private readonly Entry _start = Ui.Entry("start (inclusive)", 130);
    private readonly Entry _end = Ui.Entry("end (exclusive)", 130);
    private readonly Entry _limit = Ui.Entry("limit", 70, "100");
    private readonly ObservableCollection<KeyValueItem> _entries = [];
    private readonly Label _entriesInfo = Ui.Text(string.Empty, 12, color: Ui.Muted);
    private readonly ResultTableView _keySpaces = new() { HeightRequest = 120 };
    private readonly MessageList _messages = new();
    private KeyValueWorkspace? _workspace;

    public KeyValuePage(StudioState state)
    {
        _state = state;
        _databaseBar = new DatabaseBar(message => DisplayAlertAsync("Confirm", message, "Yes", "No"), _messages.Error);
        _databaseBar.DatabaseChanged += (_, _) =>
        {
            _transactionBar!.Refresh();
            _entries.Clear();
        };
        _transactionBar = new TransactionBar(text => _messages.Add(text, MessageKind.Success), _messages.Error);

        var entries = new CollectionView
        {
            ItemsSource = _entries,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontFamily = Ui.Mono, FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1, Padding = new Thickness(4, 2) };
                label.BindingContextChanged += (_, _) =>
                {
                    if (label.BindingContext is KeyValueItem item)
                    {
                        label.Text = item.Summary;
                    }
                };
                return label;
            }),
        };
        entries.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is KeyValueItem item)
            {
                Load(item);
            }
        };

        View editorPanel = Ui.Panel(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                Ui.Heading("Entry"),
                Ui.Bar(Ui.Text("Key", bold: true), _key, _keyEncoding),
                Ui.Bar(Ui.Text("Value", bold: true), _valueEncoding),
                _value,
                Ui.Bar(Ui.Text("Put condition", bold: true), _condition, _etag),
                Ui.Bar(
                    Ui.Button("Get", GetAsync, _messages.Error),
                    Ui.Button("Put", PutAsync, _messages.Error),
                    Ui.Button("Delete", DeleteAsync, _messages.Error),
                    Ui.Button("Exists", ExistsAsync, _messages.Error)),
                Ui.Text("Delete uses the ETag box as its expected ETag when 'If ETag matches' is chosen.", 11, color: Ui.Muted),
                Ui.Heading("Scan"),
                Ui.Text("Prefix OR start/end (the engine rejects both). Bounds use the key encoding.", 11, color: Ui.Muted),
                Ui.Bar(_prefix, _start, _end, _limit),
                Ui.Bar(
                    Ui.Button("Scan", () => ScanAsync(all: false), _messages.Error),
                    Ui.Button("Scan all", () => ScanAsync(all: true), _messages.Error)),
                Ui.Heading("Catalog"),
                Ui.Bar(Ui.Button("KEYSPACES", KeySpacesAsync, _messages.Error), Ui.Text("(engine session: embedded / loopback only)", 11, color: Ui.Muted)),
            },
        });

        View right = Ui.Rows(
            (GridLength.Auto, Ui.Bar(Ui.Heading("Entries"), _entriesInfo)),
            (new GridLength(3, GridUnitType.Star), new Border { Content = entries, Stroke = Ui.Border, StrokeThickness = 1 }),
            (GridLength.Auto, Ui.Heading("KEYSPACES")),
            (GridLength.Auto, _keySpaces),
            (new GridLength(2, GridUnitType.Star), _messages));

        var root = new Grid
        {
            Padding = new Thickness(8),
            RowSpacing = 4,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
        };
        root.Add(_databaseBar, 0, 0);
        root.Add(_transactionBar, 0, 1);
        root.Add(Ui.Columns((new GridLength(470), new ScrollView { Content = editorPanel }), (GridLength.Star, right)), 0, 2);
        Content = root;

        _state.WorkspacesChanged += (_, _) => Ui.OnMain(() => _ = BindAsync());
        _ = BindAsync();
    }

    private async Task BindAsync()
    {
        try
        {
            _workspace = _state.Get<KeyValueWorkspace>(StudioModel.KeyValue);
            _transactionBar.Bind(_workspace);
            _entries.Clear();
            await _databaseBar.BindAsync(_workspace, _state.Engines is null ? "Engines are starting (see the Workspace page)..." : $"Key-Value: {_state.GetStatus(StudioModel.KeyValue)}");
        }
        catch (Exception exception)
        {
            // Binding reports its failure in Messages.
            _messages.Error(exception);
        }
    }

    private KeyValueWorkspace Workspace()
    {
        KeyValueWorkspace workspace = _workspace ?? throw new InvalidOperationException("The Key-Value workspace is not ready (see the Workspace page).");
        return workspace.CurrentDatabase is null ? throw new InvalidOperationException("Select or create a database first.") : workspace;
    }

    private byte[] Key() => string.IsNullOrEmpty(_key.Text)
        ? throw new InvalidOperationException("Enter a key.")
        : Bytes.Parse(_key.Text, _keyEncoding.SelectedIndex == 1);

    private byte[]? Bound(Entry entry) => string.IsNullOrEmpty(entry.Text) ? null : Bytes.Parse(entry.Text, _keyEncoding.SelectedIndex == 1);

    private long? ETag() => string.IsNullOrWhiteSpace(_etag.Text)
        ? null
        : long.Parse(_etag.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private void Load(KeyValueItem item)
    {
        bool keyText = Bytes.TryUtf8(item.Key, out string key);
        _keyEncoding.SelectedIndex = keyText ? 0 : 1;
        _key.Text = keyText ? key : Convert.ToHexString(item.Key);

        bool valueText = Bytes.TryUtf8(item.Value, out string value);
        _valueEncoding.SelectedIndex = valueText ? 0 : 1;
        _value.Text = valueText ? value : Convert.ToHexString(item.Value);
        _etag.Text = item.ETag.ToString(CultureInfo.InvariantCulture);
    }

    private async Task GetAsync()
    {
        KeyValueWorkspace workspace = Workspace();
        byte[] key = Key();
        KeyValueItem? item = await Task.Run(() => workspace.GetAsync(key));
        if (item is null)
        {
            _messages.Add($"GET {Bytes.ToDisplay(key)}: not found", MessageKind.Warning);
            return;
        }

        Load(item);
        _messages.Add($"GET {item.KeyText}: {item.Value.Length} bytes, etag {item.ETag}", MessageKind.Success);
    }

    private async Task PutAsync()
    {
        KeyValueWorkspace workspace = Workspace();
        byte[] key = Key();
        byte[] value = Bytes.Parse(_value.Text, _valueEncoding.SelectedIndex == 1);
        var condition = (KeyValueCondition)Math.Max(0, _condition.SelectedIndex);
        long? expected = condition == KeyValueCondition.IfETagMatches ? ETag() : null;
        (bool applied, long? etag) = await Task.Run(() => workspace.PutAsync(key, value, condition, expected));
        if (applied && etag is { } current)
        {
            _etag.Text = current.ToString(CultureInfo.InvariantCulture);
        }

        _messages.Add(
            $"PUT {Bytes.ToDisplay(key)} ({_condition.SelectedItem}): {(applied ? "applied" : "NOT applied (condition failed)")}, etag {etag?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}",
            applied ? MessageKind.Success : MessageKind.Warning);
    }

    private async Task DeleteAsync()
    {
        KeyValueWorkspace workspace = Workspace();
        byte[] key = Key();
        long? expected = _condition.SelectedIndex == (int)KeyValueCondition.IfETagMatches ? ETag() : null;
        bool deleted = await Task.Run(() => workspace.DeleteAsync(key, expected));
        _messages.Add($"DELETE {Bytes.ToDisplay(key)}{(expected is null ? string.Empty : $" if etag {expected}")}: {(deleted ? "deleted" : "not deleted (missing or etag mismatch)")}", deleted ? MessageKind.Success : MessageKind.Warning);
    }

    private async Task ExistsAsync()
    {
        KeyValueWorkspace workspace = Workspace();
        byte[] key = Key();
        bool exists = await Task.Run(() => workspace.ExistsAsync(key));
        _messages.Add($"EXISTS {Bytes.ToDisplay(key)}: {exists}", MessageKind.Info);
    }

    private async Task ScanAsync(bool all)
    {
        KeyValueWorkspace workspace = Workspace();
        int? limit = string.IsNullOrWhiteSpace(_limit.Text) ? null : int.Parse(_limit.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
        KeyValueScan scan = all ? new KeyValueScan(null, null, null, limit) : new KeyValueScan(Bound(_prefix), Bound(_start), Bound(_end), limit);
        List<KeyValueItem> items = await Task.Run(() => workspace.ScanAsync(scan));
        _entries.Clear();
        foreach (KeyValueItem item in items)
        {
            _entries.Add(item);
        }

        _entriesInfo.Text = $"{items.Count} entries{(limit is { } max && items.Count >= max ? $" (limit {max} reached)" : string.Empty)}; click one to load it";
        _messages.Add($"SCAN: {items.Count} entries", MessageKind.Success);
    }

    private async Task KeySpacesAsync()
    {
        KeyValueWorkspace workspace = Workspace();
        TabularResult table = await Task.Run(() => workspace.KeySpacesAsync());
        _keySpaces.Show(table);
        _messages.Add($"KEYSPACES: {table.Rows.Count} rows", MessageKind.Success);
    }
}
