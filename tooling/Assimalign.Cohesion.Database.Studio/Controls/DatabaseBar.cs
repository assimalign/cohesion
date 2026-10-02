using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Maui.Controls;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// Database list / create / drop / select for one model workspace. Against an external server the
/// list is unavailable (the wire has no database-management verbs), so the name is typed instead.
/// </summary>
internal sealed class DatabaseBar : ContentView
{
    private readonly Picker _databases = Ui.Picker([], 200);
    private readonly Entry _newName = Ui.Entry("new database name", 170);
    private readonly Entry _externalName = Ui.Entry("database on the server", 200);
    private readonly Label _status = Ui.Text("starting...", 12, color: Ui.Muted);
    private readonly View _managed;
    private readonly View _external;
    private readonly Func<string, Task<bool>> _confirm;
    private ModelWorkspace? _workspace;
    private bool _suppress;

    public DatabaseBar(Func<string, Task<bool>> confirm, Action<Exception> onError)
    {
        _confirm = confirm;
        _databases.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppress || _workspace is null || _databases.SelectedItem is not string name)
            {
                return;
            }

            try
            {
                await _workspace.UseDatabaseAsync(name);
                DatabaseChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // Opening a database is user input; report it, keep the page alive.
                onError(exception);
            }
            finally
            {
                UpdateStatus();
            }
        };

        _managed = Ui.Bar(
            Ui.Text("Database:", bold: true),
            _databases,
            Ui.Button("Refresh", () => RefreshAsync(discover: false), onError),
            Ui.Button("Discover on disk", () => RefreshAsync(discover: true), onError),
            _newName,
            Ui.Button("Create", CreateAsync, onError),
            Ui.Button("Drop selected", DropAsync, onError));

        _external = Ui.Bar(
            Ui.Text("Database:", bold: true),
            _externalName,
            Ui.Button("Connect", ConnectExternalAsync, onError),
            Ui.Text("(external server: list/create/drop are engine-side only)", 12, color: Ui.Muted));

        Content = new VerticalStackLayout { Spacing = 2, Children = { _managed, _external, _status } };
        _external.IsVisible = false;
    }

    /// <summary>Raised after the current database changed (a session opened or a client connected).</summary>
    public event EventHandler? DatabaseChanged;

    public ModelWorkspace? Workspace => _workspace;

    public async Task BindAsync(ModelWorkspace? workspace, string statusWhenMissing)
    {
        _workspace = workspace;
        _managed.IsVisible = workspace?.CanManageDatabases ?? true;
        _external.IsVisible = workspace is { CanManageDatabases: false };
        IsEnabled = workspace is not null;

        if (workspace is null)
        {
            _status.Text = statusWhenMissing;
            SetItems([], null);
            return;
        }

        UpdateStatus();
        if (workspace.CanManageDatabases)
        {
            await RefreshAsync(discover: true);
        }
    }

    public void UpdateStatus()
    {
        if (_workspace is null)
        {
            return;
        }

        _status.Text = $"{_workspace.Description}   |   current database: {_workspace.CurrentDatabase ?? "(none: pick or create one)"}";
    }

    private async Task RefreshAsync(bool discover)
    {
        if (_workspace is null)
        {
            return;
        }

        IReadOnlyList<string> names = await _workspace.ListDatabasesAsync(discover);
        SetItems(names, _workspace.CurrentDatabase);

        // Auto-select the only/first database so the page is usable immediately.
        if (_workspace.CurrentDatabase is null && names.Count > 0)
        {
            _databases.SelectedIndex = 0;
        }
    }

    private void SetItems(IReadOnlyList<string> names, string? current)
    {
        _suppress = true;
        try
        {
            _databases.ItemsSource = names.ToList();
            _databases.SelectedIndex = current is null ? -1 : names.ToList().FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppress = false;
        }
    }

    private async Task CreateAsync()
    {
        if (_workspace is null || string.IsNullOrWhiteSpace(_newName.Text))
        {
            return;
        }

        string name = _newName.Text.Trim();
        await _workspace.CreateDatabaseAsync(name);
        _newName.Text = string.Empty;
        await RefreshAsync(discover: false);
        _databases.SelectedIndex = ((List<string>)_databases.ItemsSource).FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DropAsync()
    {
        if (_workspace is null || _databases.SelectedItem is not string name)
        {
            return;
        }

        if (!await _confirm($"Drop database '{name}'? Its files are deleted."))
        {
            return;
        }

        await _workspace.DropDatabaseAsync(name);
        UpdateStatus();
        await RefreshAsync(discover: false);
        DatabaseChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ConnectExternalAsync()
    {
        if (_workspace is null || string.IsNullOrWhiteSpace(_externalName.Text))
        {
            return;
        }

        try
        {
            await _workspace.UseDatabaseAsync(_externalName.Text.Trim());
        }
        finally
        {
            // A failed connect has already closed the previous connection.
            UpdateStatus();
        }

        DatabaseChanged?.Invoke(this, EventArgs.Empty);
    }
}
