using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BoltCat.Core;
using BoltCatControl.Theming;

namespace BoltCatControl.Controls;

/// <summary>
/// S-meter balk met schaal S1…S9, +20, +40, +60.
/// Snel omhoog, rustig omlaag, zodat hij bij ruis (FT-897D: hele S-eenheden) niet flikkert.
/// Tijdens TX: geen S-meter (de radio geeft dan een zendmeter door).
/// </summary>
public sealed class SMeterBar : Control
{
    private const double BarHeight = 8;
    private double _shown;
    private bool _hasValue;
    private bool _tx;

    private static readonly (string Label, double Fraction)[] Scale =
    [
        ("1", SMeter.Fraction(-48)), ("3", SMeter.Fraction(-36)), ("5", SMeter.Fraction(-24)),
        ("7", SMeter.Fraction(-12)), ("9", SMeter.Fraction(0)),
        ("+20", SMeter.Fraction(20)), ("+40", SMeter.Fraction(40)), ("+60", SMeter.Fraction(60)),
    ];

    public SMeterBar()
    {
        Height = 24;
        MinWidth = 200;
    }

    /// <summary>Nieuwe meting in dB t.o.v. S9; null = geen S-meter beschikbaar.</summary>
    public void SetDb(int? db)
    {
        if (db is not { } d)
        {
            _hasValue = false;
        }
        else
        {
            var target = SMeter.Fraction(d);
            _shown = !_hasValue || target > _shown ? target : _shown * 0.5 + target * 0.5;
            _hasValue = true;
        }
        InvalidateVisual();
    }

    public bool Tx
    {
        get => _tx;
        set { if (_tx != value) { _tx = value; InvalidateVisual(); } }
    }

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, out var o) && o is IBrush b ? b : fallback;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var track = Res(BoltTheme.Keys.BgInput, Brushes.DimGray);
        ctx.FillRectangle(track, new Rect(0, 0, w, BarHeight), 2);

        if (!_tx && _hasValue)
        {
            var color = _shown < SMeter.Fraction(0) + 0.001 ? Res(BoltTheme.Keys.Green, Brushes.LimeGreen)
                      : _shown < SMeter.Fraction(30)        ? new SolidColorBrush(Color.Parse("#ffaa00"))
                      :                                       Res(BoltTheme.Keys.Tx, Brushes.Red);
            ctx.FillRectangle(color, new Rect(0, 0, Math.Max(2, w * _shown), BarHeight), 2);
        }

        var dim = Res(BoltTheme.Keys.TextDim, Brushes.Gray);
        var typeface = new Typeface(Ui.Mono);
        foreach (var (label, f) in Scale)
        {
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, dim);
            var x = Math.Clamp(w * f - ft.Width / 2, 0, Math.Max(0, w - ft.Width));
            ctx.DrawText(ft, new Point(x, BarHeight + 3));
        }
    }
}
