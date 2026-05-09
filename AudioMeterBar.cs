using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ffmpegplayer;

internal sealed class AudioMeterBar : Control
{
    private const double MinimumDbfs = -60;
    private const double YellowDbfs = -18;
    private const double RedDbfs = -6;
    private const double MaximumDbfs = 0;

    private double _dbfs = -90;

    public AudioMeterBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        BackColor = Color.FromArgb(13, 16, 19);
        MinimumSize = new Size(18, 80);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Dbfs
    {
        get => _dbfs;
        set
        {
            var dbfs = double.IsNaN(value) || double.IsInfinity(value)
                ? -90
                : Math.Clamp(value, -90, MaximumDbfs);

            if (Math.Abs(_dbfs - dbfs) < 0.05)
            {
                return;
            }

            _dbfs = dbfs;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.None;
        e.Graphics.Clear(Color.FromArgb(13, 16, 19));

        var meter = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        DrawZone(e.Graphics, meter, MinimumDbfs, YellowDbfs, Color.FromArgb(42, 36, 130, 78));
        DrawZone(e.Graphics, meter, YellowDbfs, RedDbfs, Color.FromArgb(60, 160, 132, 36));
        DrawZone(e.Graphics, meter, RedDbfs, MaximumDbfs, Color.FromArgb(78, 160, 52, 44));

        var visibleLevel = Math.Clamp(_dbfs, MinimumDbfs, MaximumDbfs);
        if (visibleLevel > MinimumDbfs)
        {
            DrawActiveZone(e.Graphics, meter, MinimumDbfs, Math.Min(visibleLevel, YellowDbfs), Color.FromArgb(51, 204, 104));
            DrawActiveZone(e.Graphics, meter, YellowDbfs, Math.Min(visibleLevel, RedDbfs), Color.FromArgb(236, 190, 62));
            DrawActiveZone(e.Graphics, meter, RedDbfs, visibleLevel, Color.FromArgb(234, 78, 66));
        }

        DrawTick(e.Graphics, meter, YellowDbfs);
        DrawTick(e.Graphics, meter, RedDbfs);

        using var border = new Pen(Color.FromArgb(98, 111, 120));
        e.Graphics.DrawRectangle(border, meter);
    }

    private static void DrawZone(Graphics graphics, Rectangle meter, double lowerDbfs, double upperDbfs, Color color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, GetRangeRectangle(meter, lowerDbfs, upperDbfs));
    }

    private static void DrawActiveZone(Graphics graphics, Rectangle meter, double lowerDbfs, double upperDbfs, Color color)
    {
        if (upperDbfs <= lowerDbfs)
        {
            return;
        }

        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, GetRangeRectangle(meter, lowerDbfs, upperDbfs));
    }

    private static void DrawTick(Graphics graphics, Rectangle meter, double dbfs)
    {
        var y = (float)Math.Round(DbfsToY(meter, dbfs));
        using var pen = new Pen(Color.FromArgb(150, 239, 244, 248));
        graphics.DrawLine(pen, meter.Left, y, meter.Right, y);
    }

    private static RectangleF GetRangeRectangle(Rectangle meter, double lowerDbfs, double upperDbfs)
    {
        var top = (float)DbfsToY(meter, upperDbfs);
        var bottom = (float)DbfsToY(meter, lowerDbfs);
        return new RectangleF(meter.Left, top, meter.Width, Math.Max(0, bottom - top));
    }

    private static double DbfsToY(Rectangle meter, double dbfs)
    {
        var clamped = Math.Clamp(dbfs, MinimumDbfs, MaximumDbfs);
        var normalized = (clamped - MinimumDbfs) / (MaximumDbfs - MinimumDbfs);
        return meter.Bottom - normalized * meter.Height;
    }
}
