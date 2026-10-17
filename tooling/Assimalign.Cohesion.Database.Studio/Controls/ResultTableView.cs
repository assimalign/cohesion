using System;
using System.Linq;
using System.Text;

using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// A result grid with runtime columns: a header row plus a virtualized CollectionView of rows, all
/// inside a horizontal scroller. Tapping a row raises <see cref="RowTapped"/> with the full values.
/// </summary>
internal sealed class ResultTableView : ContentView
{
    private const double charWidth = 7.3;

    private readonly Grid _header = new() { ColumnSpacing = 0, BackgroundColor = Color.FromArgb("#E8EAF2") };
    private readonly CollectionView _rows = new() { SelectionMode = SelectionMode.Single };
    private readonly Grid _layout;
    private readonly Label _empty = Ui.Text("(no result set)", 12, color: Ui.Muted);
    private TabularResult? _table;

    public ResultTableView()
    {
        _layout = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            HorizontalOptions = LayoutOptions.Start,
        };
        _layout.Add(_header, 0, 0);
        _layout.Add(_rows, 0, 1);

        _rows.SelectionChanged += (_, args) =>
        {
            if (_table is not null && args.CurrentSelection.FirstOrDefault() is string[] row)
            {
                var builder = new StringBuilder();
                for (int i = 0; i < _table.Columns.Count && i < row.Length; i++)
                {
                    builder.Append(_table.Columns[i]).Append(" = ").AppendLine(row[i]);
                }

                RowTapped?.Invoke(this, builder.ToString());
            }

            _rows.SelectedItem = null;
        };

        var scroll = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _layout };
        var host = new Grid();
        host.Add(scroll);
        host.Add(_empty);
        _empty.HorizontalOptions = LayoutOptions.Center;
        _empty.VerticalOptions = LayoutOptions.Center;
        Content = new Border { Content = host, Stroke = Ui.Border, StrokeThickness = 1, BackgroundColor = Colors.White };
    }

    /// <summary>Raised with "column = value" lines for the tapped row.</summary>
    public event EventHandler<string>? RowTapped;

    public void Show(TabularResult? table)
    {
        _table = table;
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        _rows.ItemsSource = null;

        if (table is null || table.Columns.Count == 0)
        {
            _empty.IsVisible = true;
            _empty.Text = table is null ? "(no result set)" : "(result has no columns)";
            _layout.WidthRequest = -1;
            return;
        }

        _empty.IsVisible = table.Rows.Count == 0;
        _empty.Text = "(0 rows)";

        double[] widths = new double[table.Columns.Count];
        for (int column = 0; column < widths.Length; column++)
        {
            int longest = Math.Max(table.Columns[column].Length, column < table.ColumnTypes.Count ? table.ColumnTypes[column].Length : 0);
            foreach (string[] row in table.Rows.Take(300))
            {
                if (column < row.Length)
                {
                    longest = Math.Max(longest, row[column].Length);
                }
            }

            widths[column] = Math.Clamp(longest * charWidth + 16, 56, table.Columns[column] == "path" ? 1600 : 460);
            _header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(widths[column])));
            var name = new Label
            {
                FontFamily = Ui.Mono,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                Padding = new Thickness(6, 3, 6, 0),
                LineBreakMode = LineBreakMode.TailTruncation,
                Text = table.Columns[column],
            };
            var type = new Label
            {
                FontFamily = Ui.Mono,
                FontSize = 10,
                TextColor = Ui.Muted,
                Padding = new Thickness(6, 0, 6, 3),
                LineBreakMode = LineBreakMode.TailTruncation,
                Text = column < table.ColumnTypes.Count ? table.ColumnTypes[column] : string.Empty,
            };
            var cell = new VerticalStackLayout { Spacing = 0, Children = { name, type } };
            _header.Add(cell, column, 0);
        }

        _rows.ItemTemplate = new DataTemplate(() => CreateRow(widths));
        _rows.ItemsSource = table.Rows;
        _layout.WidthRequest = widths.Sum();
    }

    private static View CreateRow(double[] widths)
    {
        var grid = new Grid { ColumnSpacing = 0, HeightRequest = 22 };
        var labels = new Label[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(widths[i])));
            labels[i] = new Label
            {
                FontFamily = Ui.Mono,
                FontSize = 12,
                Padding = new Thickness(6, 2),
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
            };
            grid.Add(labels[i], i, 0);
        }

        grid.BindingContextChanged += (_, _) =>
        {
            if (grid.BindingContext is string[] row)
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    string text = i < row.Length ? row[i] : string.Empty;
                    labels[i].Text = text.ReplaceLineEndings(" ");
                    labels[i].TextColor = text == ValueFormatter.NullText ? Ui.Muted : Colors.Black;
                }
            }
        };

        return grid;
    }
}
