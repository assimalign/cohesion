using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Small code-only UI helpers shared by every page.</summary>
internal static class Ui
{
    public const string Mono = "Consolas";

    public static readonly Color Muted = Color.FromArgb("#666666");
    public static readonly Color Good = Color.FromArgb("#1B7F3A");
    public static readonly Color Bad = Color.FromArgb("#B3261E");
    public static readonly Color Warn = Color.FromArgb("#9A6700");
    public static readonly Color Accent = Color.FromArgb("#3949AB");
    public static readonly Color PanelBackground = Color.FromArgb("#F4F5F8");
    public static readonly Color Border = Color.FromArgb("#D0D4DC");

    public static Label Text(string text, double size = 13, bool bold = false, Color? color = null) => new()
    {
        Text = text,
        FontSize = size,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        TextColor = color ?? Colors.Black,
        VerticalOptions = LayoutOptions.Center,
    };

    public static Label Heading(string text) => Text(text, 15, bold: true, color: Accent);

    public static Button Button(string text, Func<Task> onClick, Action<Exception>? onError = null)
    {
        var button = new Button
        {
            Text = text,
            Padding = new Thickness(10, 2),
            MinimumHeightRequest = 30,
            HeightRequest = 32,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
        };

        button.Clicked += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await onClick();
            }
            catch (Exception exception)
            {
                // UI handlers never throw: report where the user is looking.
                if (onError is not null)
                {
                    onError(exception);
                }
                else
                {
                    CrashLog.Write($"Button '{text}'", exception);
                }
            }
            finally
            {
                button.IsEnabled = true;
            }
        };

        return button;
    }

    public static Entry Entry(string placeholder, double width = 160, string? text = null) => new()
    {
        Placeholder = placeholder,
        Text = text,
        WidthRequest = width,
        FontSize = 13,
        VerticalOptions = LayoutOptions.Center,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
    };

    public static Picker Picker(IList<string> items, double width = 160, int selected = 0) => new()
    {
        ItemsSource = new List<string>(items),
        SelectedIndex = items.Count == 0 ? -1 : selected,
        WidthRequest = width,
        FontSize = 13,
        VerticalOptions = LayoutOptions.Center,
    };

    /// <summary>A check box with its caption; WinUI's default 120px minimum width is removed.</summary>
    public static View CheckBox(string caption, bool isChecked, out CheckBox checkBox)
    {
        var box = new CheckBox { IsChecked = isChecked, VerticalOptions = LayoutOptions.Center };
        box.HandlerChanged += (_, _) =>
        {
            if (box.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.CheckBox native)
            {
                native.MinWidth = 0;
                native.Padding = new Microsoft.UI.Xaml.Thickness(0);
            }
        };

        checkBox = box;
        return new HorizontalStackLayout { Spacing = 4, Children = { box, Text(caption, 12) } };
    }

    public static FlexLayout Bar(params View[] views)
    {
        var bar = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
            AlignContent = Microsoft.Maui.Layouts.FlexAlignContent.Start,
        };

        foreach (View view in views)
        {
            view.Margin = new Thickness(0, 2, 6, 2);
            bar.Children.Add(view);
        }

        return bar;
    }

    public static Border Panel(View content, Thickness? padding = null) => new()
    {
        Content = content,
        Padding = padding ?? new Thickness(6),
        Stroke = Border,
        StrokeThickness = 1,
        BackgroundColor = PanelBackground,
    };

    public static Grid Rows(params (GridLength Height, View View)[] rows)
    {
        var grid = new Grid { RowSpacing = 4 };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(rows[i].Height));
            grid.Add(rows[i].View, 0, i);
        }

        return grid;
    }

    public static Grid Columns(params (GridLength Width, View View)[] columns)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < columns.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(columns[i].Width));
            grid.Add(columns[i].View, i, 0);
        }

        return grid;
    }

    public static void OnMain(Action action) => MainThread.BeginInvokeOnMainThread(() =>
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            // Background notifications must never tear down the UI thread.
            CrashLog.Write("Ui.OnMain", exception);
        }
    });

    /// <summary>Monospace, read-only, selectable multi-line text.</summary>
    public static Editor ReadOnlyText(double height = -1) => new()
    {
        IsReadOnly = true,
        FontFamily = Mono,
        FontSize = 12,
        HeightRequest = height,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
    };
}
