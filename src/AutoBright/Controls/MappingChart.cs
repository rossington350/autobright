using AutoBright.Controller;

namespace AutoBright.Controls;

/// <summary>
/// Draws the lux → brightness mapping (flat at MinBrightness below MinLux, a
/// linear ramp up to MaxLux, flat at MaxBrightness above it) with a marker at
/// the live reading. In manual mode the curve is dimmed and a horizontal line
/// shows the manual target instead.
/// </summary>
public sealed class MappingChart : IDrawable
{
    public ConfigData? Config { get; set; }
    public double? Lux { get; set; }
    public int? TargetBrightness { get; set; }

    public Color Accent { get; set; } = Colors.SlateBlue;
    public Color Muted { get; set; } = Colors.Gray;
    public Color Grid { get; set; } = Colors.LightGray;
    public Color MarkerRing { get; set; } = Colors.White;

    private const float PadLeft = 34;
    private const float PadRight = 10;
    private const float PadTop = 8;
    private const float PadBottom = 18;

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (Config is not ConfigData cfg)
        {
            return;
        }

        var plot = new RectF(
            rect.Left + PadLeft,
            rect.Top + PadTop,
            Math.Max(1, rect.Width - PadLeft - PadRight),
            Math.Max(1, rect.Height - PadTop - PadBottom));

        // Leave headroom right of MaxLux so the "flat above max" part is visible.
        double xMax = Math.Max(cfg.MaxLux * 1.3, cfg.MaxLux + 10);
        float X(double lux) => plot.Left + (float)(Math.Clamp(lux / xMax, 0, 1) * plot.Width);
        float Y(double pct) => plot.Bottom - (float)(Math.Clamp(pct / 100.0, 0, 1) * plot.Height);

        canvas.Antialias = true;
        canvas.FontSize = 10;

        // Grid: 0 / 50 / 100 % lines with labels.
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1;
        canvas.FontColor = Muted;
        foreach (int pct in new[] { 0, 50, 100 })
        {
            float y = Y(pct);
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            canvas.DrawString($"{pct}%", rect.Left, y - 6, PadLeft - 6, 12,
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }

        // Threshold ticks on the x axis.
        canvas.StrokeDashPattern = new float[] { 2, 3 };
        foreach (double threshold in new[] { cfg.MinLux, cfg.MaxLux })
        {
            float x = X(threshold);
            canvas.DrawLine(x, plot.Top, x, plot.Bottom);
            canvas.DrawString($"{threshold:0.#} lx", x - 40, plot.Bottom + 3, 80, 12,
                HorizontalAlignment.Center, VerticalAlignment.Top);
        }
        canvas.StrokeDashPattern = null;

        // The mapping curve, with a soft fill underneath.
        var points = new[]
        {
            new PointF(X(0), Y(cfg.MinBrightness)),
            new PointF(X(cfg.MinLux), Y(cfg.MinBrightness)),
            new PointF(X(cfg.MaxLux), Y(cfg.MaxBrightness)),
            new PointF(X(xMax), Y(cfg.MaxBrightness)),
        };
        bool manual = cfg.ManualControl;
        Color curve = manual ? Muted : Accent;

        var fill = new PathF(points[0].X, plot.Bottom);
        foreach (PointF p in points)
        {
            fill.LineTo(p);
        }
        fill.LineTo(points[^1].X, plot.Bottom);
        fill.Close();
        canvas.FillColor = curve.WithAlpha(0.12f);
        canvas.FillPath(fill);

        var line = new PathF(points[0]);
        for (int i = 1; i < points.Length; i++)
        {
            line.LineTo(points[i]);
        }
        canvas.StrokeColor = curve.WithAlpha(manual ? 0.6f : 1f);
        canvas.StrokeSize = 2;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(line);

        if (manual)
        {
            if (TargetBrightness is int manualTarget)
            {
                float y = Y(manualTarget);
                canvas.StrokeColor = Accent;
                canvas.StrokeSize = 2;
                canvas.StrokeDashPattern = new float[] { 5, 3 };
                canvas.DrawLine(plot.Left, y, plot.Right, y);
                canvas.StrokeDashPattern = null;
                canvas.FontColor = Accent;
                canvas.DrawString($"Manual {manualTarget}%", plot.Left + 4, y - 14, plot.Width - 8, 12,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
            }
            return;
        }

        // Live marker, placed on the curve at the current reading.
        if (Lux is double reading && BrightnessController.ComputeBrightness(cfg, reading) is int mapped)
        {
            float x = X(reading);
            float y = Y(mapped);
            canvas.FillColor = Accent;
            canvas.FillCircle(x, y, 5.5f);
            canvas.StrokeColor = MarkerRing;
            canvas.StrokeSize = 2;
            canvas.DrawCircle(x, y, 5.5f);
        }
    }
}
