// Painted controls for the key-art layout: the art itself (cover-scaled, cached), the fading bottom bar,
// a translucent panel for the log drawer and a title label with a soft halo for light backgrounds.
// WinForms draws a control with a transparent or ARGB BackColor by first painting its parent, so these
// all sit visually on top of the art without any per-pixel window tricks.

using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AAEmuLauncher;

/// <summary>
/// Paints an image scaled to cover the control (crop, never stretch), keeping a focal point of the image
/// as close as possible to the same relative spot in the control. The scaled copy is cached per size.
/// </summary>
internal sealed class ArtPanel : Panel
{
    private readonly Image _art;
    private readonly PointF _focus;
    private Bitmap _cache;
    private bool _fastScaling;

    public ArtPanel(Image art, PointF focus)
    {
        _art = art;
        _focus = focus;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(24, 20, 17); // shown only when the art could not be loaded
    }

    /// <summary>True while the user drags the window edge: cheaper scaling, redone in full quality afterwards.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FastScaling
    {
        get => _fastScaling;
        set
        {
            if (_fastScaling == value)
                return;
            _fastScaling = value;
            if (!value)
            {
                DropCache();
                Invalidate(true);
            }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        DropCache();
        base.OnResize(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_art == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            base.OnPaintBackground(e);
            return;
        }
        _cache ??= Cover(_art, ClientSize, _focus, _fastScaling);
        e.Graphics.DrawImageUnscaled(_cache, 0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DropCache();
        base.Dispose(disposing);
    }

    private void DropCache()
    {
        _cache?.Dispose();
        _cache = null;
    }

    private static Bitmap Cover(Image source, Size size, PointF focus, bool fast)
    {
        var scale = Math.Max((float)size.Width / source.Width, (float)size.Height / source.Height);
        float width = source.Width * scale, height = source.Height * scale;
        var x = Math.Clamp(focus.X * (size.Width - width), size.Width - width, 0);
        var y = Math.Clamp(focus.Y * (size.Height - height), size.Height - height, 0);
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = fast ? CompositingQuality.HighSpeed : CompositingQuality.HighQuality;
        graphics.DrawImage(source, new RectangleF(x, y, width, height));
        return bitmap;
    }
}

/// <summary>Bottom bar: clear at its top edge, fading into <see cref="Tint"/> over <see cref="FadeHeight"/> px.</summary>
internal sealed class FadePanel : Panel
{
    public FadePanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Tint { get; set; } = Color.FromArgb(220, 18, 15, 12);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int FadeHeight { get; set; } = 50;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e); // the art behind the bar
        var fade = Math.Min(FadeHeight, Height);
        if (fade > 0)
        {
            using var brush = new LinearGradientBrush(new Rectangle(0, -1, Math.Max(Width, 1), fade + 1),
                Color.FromArgb(0, Tint), Tint, LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(brush, 0, 0, Width, fade);
        }
        using var solid = new SolidBrush(Tint);
        e.Graphics.FillRectangle(solid, 0, fade, Width, Height - fade);
    }
}

/// <summary>Panel whose ARGB BackColor is drawn over whatever its parent paints (the art).</summary>
internal sealed class GlassPanel : Panel
{
    public GlassPanel(Color tint)
    {
        DoubleBuffered = true;
        BackColor = tint;
    }
}

/// <summary>Layout table with a transparent background, so the bar or the art shows through it.</summary>
internal sealed class ClearTable : TableLayoutPanel
{
    public ClearTable()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Margin = Padding.Empty;
    }
}

/// <summary>Label drawn with a soft dark halo, so light text stays readable on the light watercolour art.</summary>
internal sealed class ShadowLabel : Label
{
    public ShadowLabel()
    {
        BackColor = Color.Transparent;
        AutoSize = true;
        Padding = new Padding(2, 2, 6, 4);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Halo { get; set; } = Color.FromArgb(60, 20, 10, 40);

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float x = Padding.Left, y = Padding.Top;
        using (var halo = new SolidBrush(Halo))
        {
            for (var dx = -2; dx <= 3; dx++)
                for (var dy = -2; dy <= 3; dy++)
                    if (dx * dx + dy * dy <= 8)
                        graphics.DrawString(Text, Font, halo, x + dx, y + dy, StringFormat.GenericTypographic);
        }
        using var fore = new SolidBrush(ForeColor);
        graphics.DrawString(Text, Font, fore, x, y, StringFormat.GenericTypographic);
    }
}
