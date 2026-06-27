using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace ffmpegplayer;

internal sealed class ThemedCheckBox : CheckBox
{
    private bool _darkMode = true;

    public ThemedCheckBox()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        FlatStyle = FlatStyle.Flat;
    }

    [DefaultValue(true)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DarkMode
    {
        get => _darkMode;
        set
        {
            if (_darkMode == value)
            {
                return;
            }

            _darkMode = value;
            Invalidate();
        }
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var glyphSize = 14;
        var glyphBounds = new Rectangle(
            0,
            Math.Max(0, (Height - glyphSize) / 2),
            glyphSize,
            glyphSize);

        var borderColor = _darkMode
            ? Enabled ? Color.FromArgb(224, 232, 236) : Color.FromArgb(176, 188, 198)
            : Enabled ? Color.FromArgb(48, 56, 64) : Color.FromArgb(120, 130, 140);
        var fillColor = _darkMode
            ? Color.FromArgb(16, 20, 24)
            : Color.White;

        using (var fill = new SolidBrush(fillColor))
        {
            e.Graphics.FillRectangle(fill, glyphBounds);
        }

        using (var border = new Pen(borderColor))
        {
            e.Graphics.DrawRectangle(border, glyphBounds);
        }

        if (Checked)
        {
            var checkColor = _darkMode
                ? Color.White
                : Color.FromArgb(18, 23, 28);
            using var pen = new Pen(checkColor, 2f);
            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            e.Graphics.DrawLines(
                pen,
                [
                    new Point(glyphBounds.Left + 3, glyphBounds.Top + 7),
                    new Point(glyphBounds.Left + 6, glyphBounds.Top + 10),
                    new Point(glyphBounds.Left + 11, glyphBounds.Top + 4),
                ]);
        }

        var textBounds = new Rectangle(
            glyphBounds.Right + 6,
            0,
            Math.Max(0, Width - glyphBounds.Right - 6),
            Height);
        var textColor = _darkMode
            ? Color.White
            : Enabled ? ForeColor : Color.FromArgb(24, 29, 34);

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            textColor,
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }
}
