using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BoltCatControl.Theming;

namespace BoltCatControl.Controls;

/// <summary>
/// Frequentie als losse cijfers, "14.074.000", zoals op de radio.
///  • scrollwiel op een cijfer: dat cijfer omhoog/omlaag (scroll op de 1 kHz = ±1 kHz)
///  • klik op de bovenste helft: +1, onderste helft (of rechtsklik): -1
/// </summary>
public sealed class FrequencyDisplay : StackPanel
{
    private const int DigitCount = 9; // 100 MHz … 1 Hz
    private readonly TextBlock[] _digits = new TextBlock[DigitCount];
    private readonly List<TextBlock> _separators = [];
    private long? _hz;

    /// <summary>Gevraagde verandering in Hz (positief = omhoog).</summary>
    public event Action<long>? TuneBy;

    public bool Interactive { get; set; } = true;

    public FrequencyDisplay(double fontSize, string colorKey = BoltTheme.Keys.Accent)
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Left;

        for (int i = 0; i < DigitCount; i++)
        {
            if (i is 3 or 6)
            {
                var sep = new TextBlock
                {
                    Text = ".",
                    FontFamily = Ui.Mono,
                    FontSize = fontSize,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(-fontSize * 0.12, 0),
                }.Themed(TextBlock.ForegroundProperty, colorKey);
                sep.Opacity = 0.6;
                _separators.Add(sep);
                Children.Add(sep);
            }

            long power = (long)Math.Pow(10, DigitCount - 1 - i);
            var tb = new TextBlock
            {
                Text = "-",
                FontFamily = Ui.Mono,
                FontSize = fontSize,
                FontWeight = FontWeight.SemiBold,
                Background = Brushes.Transparent,
                Padding = new Thickness(1, 0),
            }.Themed(TextBlock.ForegroundProperty, colorKey);

            ToolTip.SetTip(tb, $"Scroll of klik boven/onder: {Step(power)}");

            tb.PointerWheelChanged += (_, e) =>
            {
                if (!Interactive || _hz is null) return;
                TuneBy?.Invoke((e.Delta.Y > 0 ? 1 : -1) * power);
                e.Handled = true;
            };
            tb.PointerPressed += (_, e) =>
            {
                if (!Interactive || _hz is null) return;
                var pt = e.GetCurrentPoint(tb);
                bool down = pt.Properties.IsRightButtonPressed || pt.Position.Y > tb.Bounds.Height / 2;
                TuneBy?.Invoke((down ? -1 : 1) * power);
                e.Handled = true;
            };
            tb.PointerEntered += (_, _) =>
            {
                if (Interactive && _hz is not null)
                    tb.Themed(TextBlock.BackgroundProperty, BoltTheme.Keys.BgInput);
            };
            tb.PointerExited += (_, _) => tb.Background = Brushes.Transparent;

            _digits[i] = tb;
            Children.Add(tb);
        }
        Render(); // begintoestand: gedimde streepjes
    }

    private static string Step(long power) => power switch
    {
        >= 1_000_000 => $"{power / 1_000_000} MHz",
        >= 1_000     => $"{power / 1_000} kHz",
        _            => $"{power} Hz",
    };

    /// <summary>Frequentie in Hz, null = streepjes (niet verbonden).</summary>
    public long? Hz
    {
        get => _hz;
        set
        {
            if (_hz == value) return;
            _hz = value;
            Render();
        }
    }

    private void Render()
    {
        if (_hz is not { } hz)
        {
            foreach (var d in _digits) { d.Text = "-"; d.Opacity = 0.25; }
            foreach (var s in _separators) s.Opacity = 0.25;
            return;
        }

        var text = Math.Clamp(hz, 0, 999_999_999).ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
        // Voorloopnullen gedimd (maar het MHz-cijfer altijd helder: "  7.074.000")
        int firstSignificant = Math.Min(text.TakeWhile(c => c == '0').Count(), 2);
        for (int i = 0; i < DigitCount; i++)
        {
            _digits[i].Text = text[i].ToString();
            _digits[i].Opacity = i < firstSignificant ? 0.15 : 1.0;
        }
        _separators[0].Opacity = 0.6;
        _separators[1].Opacity = 0.6;
    }

    /// <summary>"14.074.000"</summary>
    public static string Format(long hz) =>
        $"{hz / 1_000_000}.{hz / 1_000 % 1_000:000}.{hz % 1_000:000}";
}
