using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BoltCatControl.Theming;

/// <summary>
/// Kleine bouwstenen in Bolt-stijl (BoltSDR / ATR1000 / BoltSwitch):
/// donkere panelen met dunne rand, LED-indicatoren, status-pills,
/// muted uppercase sectielabels en een monospace font voor getallen.
/// Kleuren komen uit de theme resources, dus wisselen live mee.
/// </summary>
public static class Ui
{
    public static readonly FontFamily Mono =
        new("Cascadia Mono, Consolas, JetBrains Mono, DejaVu Sans Mono, Liberation Mono");

    public static readonly CornerRadius Radius = new(6);

    /// <summary>Koppel een property aan een theme-kleur (live bij themawissel).</summary>
    public static T Themed<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    /// <summary>Paneel: BgPanel achtergrond, 1px rand, afgeronde hoeken.</summary>
    public static Border Panel(Control child, Thickness? padding = null) =>
        new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = Radius,
            Padding = padding ?? new Thickness(12, 10),
            Child = child,
        }
        .Themed(Border.BackgroundProperty, BoltTheme.Keys.BgPanel)
        .Themed(Border.BorderBrushProperty, BoltTheme.Keys.Border);

    /// <summary>Klein uppercase label boven een sectie (zoals "VFO A").</summary>
    public static TextBlock SectionLabel(string text) =>
        new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(2, 0, 0, 6),
        }
        .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.TextDim);

    public static TextBlock MonoText(string text, double size, string colorKey = BoltTheme.Keys.Text) =>
        new TextBlock { Text = text, FontFamily = Mono, FontSize = size }
            .Themed(TextBlock.ForegroundProperty, colorKey);

    /// <summary>Knop met async handler; fouten worden afgevangen (de app mag nooit crashen op een klik).</summary>
    public static Button Button(string text, Func<Task> onClick, double minWidth = 0, bool small = false)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = minWidth,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = small ? new Thickness(8, 3) : new Thickness(12, 6),
            FontSize = small ? 12 : 13,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = Radius,
            BorderThickness = new Thickness(1),
        };
        b.Click += async (_, _) =>
        {
            try { await onClick(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Knop '{text}': {ex}"); }
        };
        return b;
    }

    /// <summary>Actieve knop (huidige mode/band/stap) markeren met de accentkleur.</summary>
    public static void SetActive(Button b, bool active)
    {
        if (active)
        {
            b.Themed(Avalonia.Controls.Button.BackgroundProperty, BoltTheme.Keys.AccentDim);
            b.Themed(Avalonia.Controls.Button.BorderBrushProperty, BoltTheme.Keys.Accent);
        }
        else
        {
            b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
            b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
        }
    }

    /// <summary>Eén regel: links uitgelijnde en rechts uitgelijnde elementen.</summary>
    public static Control Row(Control[] left, Control[] right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var l = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in left) l.Children.Add(c);
        var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in right) r.Children.Add(c);
        Grid.SetColumn(r, 1);
        grid.Children.Add(l);
        grid.Children.Add(r);
        return grid;
    }
}

/// <summary>LED-indicator met label eronder; aan = gekleurd met gloed.</summary>
public sealed class Led : StackPanel
{
    private readonly Border _dot;
    private readonly string _onKey;

    public Led(string label, string onColorKey = BoltTheme.Keys.Green)
    {
        _onKey = onColorKey;
        Spacing = 3;
        HorizontalAlignment = HorizontalAlignment.Center;

        _dot = new Border
        {
            Width = 10, Height = 10,
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#2a2a3a")),
        };
        Children.Add(_dot);
        Children.Add(new TextBlock { Text = label, FontSize = 9, FontFamily = Ui.Mono }
            .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.TextDim));
    }

    public bool IsOn
    {
        set
        {
            if (value && this.TryFindResource(_onKey, out var res) && res is ISolidColorBrush brush)
            {
                _dot.Background = brush;
                _dot.BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = brush.Color });
            }
            else
            {
                _dot.Background = new SolidColorBrush(Color.Parse("#2a2a3a"));
                _dot.BoxShadow = default;
            }
        }
    }
}

/// <summary>Status-pill: bolletje + mono tekst, zoals in de ATR1000 statusbalk.</summary>
public sealed class StatusPill : Border
{
    private readonly Border _dot;
    private readonly TextBlock _text;

    public StatusPill(string text)
    {
        BorderThickness = new Thickness(1);
        CornerRadius = Ui.Radius;
        Padding = new Thickness(10, 5);
        this.Themed(BackgroundProperty, BoltTheme.Keys.BgPanel)
            .Themed(BorderBrushProperty, BoltTheme.Keys.Border);

        _dot = new Border
        {
            Width = 6, Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#2a2a3a")),
        };
        _text = new TextBlock { Text = text, FontSize = 11, FontFamily = Ui.Mono }
            .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.TextDim);

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _dot, _text },
        };
    }

    public string Text { set => _text.Text = value; }

    public bool IsOk
    {
        set => _dot.Themed(BackgroundProperty, value ? BoltTheme.Keys.Green : BoltTheme.Keys.BgInput);
    }
}
