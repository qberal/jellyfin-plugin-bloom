using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>How the friend's name is drawn on its posters (Bloom → This server → Badge style).</summary>
public enum BadgeStyle
{
    /// <summary>Dark pill with a dot, centred at the bottom (Bloom 0.1 to 0.2).</summary>
    Classic,

    /// <summary>Dark tab flush in the bottom-left corner.</summary>
    Corner,

    /// <summary>Dark gradient at the bottom, name in white.</summary>
    Scrim,

    /// <summary>Small white label, black text.</summary>
    Label,

    /// <summary>Tab in the friend's colour, with a line of that colour along the bottom.</summary>
    ColorTab,

    /// <summary>Frosted glass tinted with the friend's colour.</summary>
    Glass,

    /// <summary>Milky frosted glass, text in the friend's colour.</summary>
    Frosted,

    /// <summary>Solid label in the friend's colour.</summary>
    Pill,

    /// <summary>Frosted glass, no tint: the poster blurred behind the name.</summary>
    NeutralGlass,

    /// <summary>Label in the main colour of the bottom of the poster.</summary>
    PosterLabel,

    /// <summary>Gradient at the bottom, in the dark colour of the poster.</summary>
    PosterScrim,

    /// <summary>Frosted glass tinted with the most vivid colour of the poster.</summary>
    PosterGlass,

    /// <summary>Tab flush with the bottom edge, centred, in the friend's colour.</summary>
    Tab,

    /// <summary>Bloom's mark and the name in white, top left, no background.</summary>
    Mark,

    /// <summary>Bottom of the poster progressively blurred and tinted with its colour, mark and name.</summary>
    Cinema,

    /// <summary>Diagonal ribbon across the top-left corner, in the friend's colour.</summary>
    Ribbon,

    /// <summary>The same ribbon, in tinted glass.</summary>
    RibbonGlass,
}

/// <summary>What to draw on a friend's image.</summary>
/// <param name="Label">Friend name.</param>
/// <param name="Style">Style.</param>
/// <param name="Color">Friend colour (0xRRGGBB).</param>
public sealed record BadgeSpec(string Label, BadgeStyle Style, uint Color)
{
    /// <summary>Gets the part of a file's state tag that changes when the badge must be drawn again.</summary>
    public string Tag => string.Create(CultureInfo.InvariantCulture, $"{Style}:{Color:X6}:{Label}");
}

/// <summary>
/// Draws the friend's name on a downloaded poster or episode image, so a friend's movie is recognizable in every
/// list, on every client: the badge is part of the image itself. Uses the SkiaSharp copy shipped with Jellyfin and
/// Inter (embedded); any failure leaves the original image untouched.
/// </summary>
public static class PosterBadge
{
    private const int MaxLabelLength = 18;

    /// <summary>About 25 megapixels (100 MB decoded): a 4K backdrop is 8, a large poster 6.</summary>
    private const long MaxPixels = 25_000_000;

    // Apple's system colours (their deeper, light-appearance values: white text stays readable on all of them).
    private static readonly uint[] Palette = [0x007AFF, 0x5856D6, 0xAF52DE, 0xFF2D55, 0xE85D00, 0x248A3D, 0x00809E];

    private static readonly Lazy<SKTypeface> SemiBold = new(() => LoadTypeface("Inter-SemiBold.ttf"));
    private static readonly Lazy<SKTypeface> Bold = new(() => LoadTypeface("Inter-Bold.ttf"));
    private static readonly Lazy<SKBitmap?> MarkBitmap = new(LoadMark);

    private static int _warned;

    /// <summary>
    /// Parses a style name, falling back to <see cref="BadgeStyle.Glass"/>.
    /// </summary>
    /// <param name="name">Style name.</param>
    /// <returns>The style.</returns>
    public static BadgeStyle ParseStyle(string? name)
        => Enum.TryParse<BadgeStyle>(name, true, out var style) && Enum.IsDefined(style) ? style : BadgeStyle.Glass;

    /// <summary>
    /// The colour of a friend: stable (from its server id, so it survives renames), one of a few Apple colours.
    /// </summary>
    /// <param name="peerId">Friend's server id.</param>
    /// <returns>0xRRGGBB.</returns>
    public static uint FriendColor(string peerId)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(peerId ?? string.Empty))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return Palette[hash % (uint)Palette.Length];
    }

    /// <summary>
    /// Adds the badge to an image file, in place.
    /// </summary>
    /// <param name="path">Image file (.jpg, .png or .webp).</param>
    /// <param name="badge">What to draw.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Whether the badge was drawn.</returns>
    public static bool TryApply(string path, BadgeSpec badge, ILogger logger)
    {
        try
        {
            return Apply(path, badge);
        }
        catch (Exception ex)
        {
            // Missing native Skia, unusual image... the badge is cosmetic: keep the original poster.
            if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
            {
                logger.LogWarning(ex, "Bloom: could not draw the friend badge on {Path}", path);
            }

            return false;
        }
    }

    /// <summary>
    /// Shortens a label to fit in the badge.
    /// </summary>
    /// <param name="label">Label.</param>
    /// <returns>Shortened label.</returns>
    public static string Shorten(string label)
    {
        var trimmed = string.Join(' ', (label ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var info = new StringInfo(trimmed);
        return info.LengthInTextElements <= MaxLabelLength
            ? trimmed
            : info.SubstringByTextElements(0, MaxLabelLength - 1).TrimEnd() + "…";
    }

    /// <summary>
    /// Draws a badge on a bitmap (exposed for tests).
    /// </summary>
    /// <param name="bitmap">Bitmap (RGBA).</param>
    /// <param name="badge">What to draw.</param>
    internal static void Draw(SKBitmap bitmap, BadgeSpec badge)
    {
        var text = Shorten(badge.Label);
        if (text.Length == 0)
        {
            return;
        }

        using var canvas = new SKCanvas(bitmap);
        var d = new Drawing(canvas, bitmap, text, new SKColor(badge.Color | 0xFF000000));
        switch (badge.Style)
        {
            case BadgeStyle.Classic: d.Classic(); break;
            case BadgeStyle.Corner: d.Tab(new SKColor(14, 14, 16, 215), line: false); break;
            case BadgeStyle.Scrim: d.Scrim(SKColors.Black); break;
            case BadgeStyle.Label: d.Label(); break;
            case BadgeStyle.ColorTab: d.Tab(d.Friend, line: true); break;
            case BadgeStyle.Glass: d.Glass(d.Friend.WithAlpha(185), SKColors.White, 70); break;
            case BadgeStyle.Frosted: d.Glass(new SKColor(255, 255, 255, 200), Darker(d.Friend), 140); break;
            case BadgeStyle.Pill: d.Pill(); break;
            case BadgeStyle.NeutralGlass: d.Glass(new SKColor(10, 10, 12, 95), SKColors.White, 75); break;
            case BadgeStyle.PosterLabel: d.PosterLabel(); break;
            case BadgeStyle.PosterScrim: d.Scrim(WithValue(d.AverageColor(d.BottomArea(0.32f)), 16, 70)); break;
            case BadgeStyle.PosterGlass: d.Glass(WithValue(d.VibrantColor(), 50, 80).WithAlpha(175), SKColors.White, 75); break;
            case BadgeStyle.Tab: d.CenteredTab(); break;
            case BadgeStyle.Mark: d.Mark(); break;
            case BadgeStyle.Cinema: d.Cinema(); break;
            case BadgeStyle.Ribbon: d.Ribbon(glass: false); break;
            case BadgeStyle.RibbonGlass: d.Ribbon(glass: true); break;
            default: d.Glass(d.Friend.WithAlpha(185), SKColors.White, 70); break;
        }

        canvas.Flush();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Apply(string path, BadgeSpec badge)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => (SKEncodedImageFormat?)null
        };
        if (format is null || Shorten(badge.Label).Length == 0)
        {
            return false;
        }

        SKBitmap? bitmap;
        using (var codec = SKCodec.Create(path))
        {
            // Rotated (EXIF) images would get the badge in the wrong corner: leave them alone.
            if (codec is null || codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
            {
                return false;
            }

            // The file is at most 25 MB, but a small file can declare huge dimensions: decoding allocates
            // 4 bytes per pixel, so a crafted image could exhaust memory. Real posters are far below this.
            if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
            {
                return false;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            bitmap = new SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                bitmap.Dispose();
                return false;
            }
        }

        using (bitmap)
        {
            if (bitmap.Width < 64 || bitmap.Height < 64)
            {
                return false;
            }

            Draw(bitmap, badge);

            var tmp = path + ".badge";
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(format.Value, 90))
            {
                if (data is null)
                {
                    return false;
                }

                using var output = File.Create(tmp);
                data.SaveTo(output);
            }

            File.Move(tmp, path, true);
            return true;
        }
    }

    private static SKTypeface LoadTypeface(string name)
    {
        using var stream = typeof(PosterBadge).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Resources.{name}");
        return (stream is null ? null : SKTypeface.FromStream(stream)) ?? SKTypeface.Default;
    }

    private static SKBitmap? LoadMark()
    {
        using var stream = typeof(PosterBadge).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Resources.bloom-mark.png");
        return stream is null ? null : SKBitmap.Decode(stream);
    }

    private static SKColor Darker(SKColor c) => new((byte)(c.Red * 0.78), (byte)(c.Green * 0.78), (byte)(c.Blue * 0.78));

    // Same hue, at a given brightness (0-100) and capped saturation: dark enough for white text.
    private static SKColor WithValue(SKColor c, float value, float maxSaturation)
    {
        c.ToHsv(out var hue, out var saturation, out _);
        return SKColor.FromHsv(hue, Math.Min(saturation, maxSaturation), value);
    }

    /// <summary>One badge being drawn: sizes come from the short side, so posters and episode images match.</summary>
    private sealed class Drawing
    {
        private readonly SKCanvas _canvas;
        private readonly SKBitmap _bitmap;
        private readonly string _text;
        private readonly int _w;
        private readonly int _h;
        private readonly float _unit;

        public Drawing(SKCanvas canvas, SKBitmap bitmap, string text, SKColor friend)
        {
            _canvas = canvas;
            _bitmap = bitmap;
            _text = text;
            _w = bitmap.Width;
            _h = bitmap.Height;
            _unit = Math.Min(_w, _h);
            Friend = friend;
        }

        public SKColor Friend { get; }

        public void Classic()
        {
            using var font = Font(bold: true, _unit * 0.07f);
            var margin = _unit * 0.06f;
            var padX = font.Size * 0.6f;
            var height = font.Size * 1.7f;
            var dot = font.Size * 0.27f;
            var gap = font.Size * 0.4f;
            var width = Math.Min(padX + (dot * 2) + gap + font.MeasureText(_text) + padX, _w - (2 * margin));
            var pill = SKRect.Create((_w - width) / 2, _h - margin - height, width, height);
            using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 175) };
            _canvas.DrawRoundRect(pill, height / 2, height / 2, fill);
            fill.Color = new SKColor(0x00, 0xA4, 0xDC);
            _canvas.DrawCircle(pill.Left + padX + dot, pill.MidY, dot, fill);
            fill.Color = SKColors.White;
            _canvas.Save();
            _canvas.ClipRect(new SKRect(pill.Left, pill.Top, pill.Right - (padX / 2), pill.Bottom));
            _canvas.DrawText(_text, pill.Left + padX + (dot * 2) + gap, Baseline(font, pill.MidY), SKTextAlign.Left, font, fill);
            _canvas.Restore();
        }

        public void Tab(SKColor color, bool line)
        {
            using var fill = new SKPaint { IsAntialias = true, Color = color };
            if (line)
            {
                _canvas.DrawRect(new SKRect(0, _h - (_unit * 0.012f), _w, _h), fill);
            }

            using var font = Font(bold: false, _unit * 0.066f);
            var padX = font.Size * 0.8f;
            var height = font.Size * 2.0f;
            var width = Math.Min(font.MeasureText(_text) + (2 * padX), _w * 0.8f);
            var rect = new SKRect(0, _h - height, width, _h);
            _canvas.DrawRoundRect(Corners(rect, 0, height * 0.38f, 0, 0), fill);
            fill.Color = SKColors.White;
            DrawClipped(font, fill, padX, rect, SKTextAlign.Left);
        }

        public void CenteredTab()
        {
            using var font = Font(bold: true, _unit * 0.06f);
            var padX = font.Size * 0.7f;
            var height = font.Size * 1.75f;
            var width = Math.Min(font.MeasureText(_text) + (2 * padX), _w * 0.9f);
            var rect = new SKRect((_w - width) / 2, _h - height, (_w + width) / 2, _h);
            var r = height * 0.22f;
            using var fill = new SKPaint { IsAntialias = true, Color = Friend };
            _canvas.DrawRoundRect(Corners(rect, r, r, 0, 0), fill);
            fill.Color = SKColors.White;
            DrawClipped(font, fill, padX, rect, SKTextAlign.Center);
        }

        public void Scrim(SKColor color)
        {
            var top = _h - (_unit * 0.3f);
            using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, _h), [color.WithAlpha(0), color.WithAlpha(210)], SKShaderTileMode.Clamp))
            using (var scrim = new SKPaint { Shader = shader })
            {
                _canvas.DrawRect(new SKRect(0, top, _w, _h), scrim);
            }

            using var font = Font(bold: false, _unit * 0.068f);
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
            _canvas.DrawText(_text, _unit * 0.06f, _h - (_unit * 0.06f), SKTextAlign.Left, font, paint);
        }

        public void Label()
        {
            using var font = Font(bold: false, _unit * 0.062f);
            var rect = LabelRect(font, out var padX);
            var rr = new SKRoundRect(rect, rect.Height * 0.2f, rect.Height * 0.2f);
            Shadow(rr, font.Size);
            using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(250, 250, 250) };
            _canvas.DrawRoundRect(rr, fill);
            fill.Color = new SKColor(20, 20, 22);
            DrawClipped(font, fill, padX, rect, SKTextAlign.Left);
        }

        public void Pill()
        {
            using var font = Font(bold: false, _unit * 0.062f);
            var rect = LabelRect(font, out var padX);
            var rr = new SKRoundRect(rect, rect.Height * 0.3f, rect.Height * 0.3f);
            Shadow(rr, font.Size);
            using var fill = new SKPaint { IsAntialias = true, Color = Friend };
            _canvas.DrawRoundRect(rr, fill);
            Highlight(rr, font.Size, 60);
            fill.Color = SKColors.White;
            DrawClipped(font, fill, padX, rect, SKTextAlign.Left);
        }

        public void PosterLabel()
        {
            using var font = Font(bold: false, _unit * 0.062f);
            var rect = LabelRect(font, out var padX);
            var rr = new SKRoundRect(rect, rect.Height * 0.3f, rect.Height * 0.3f);
            var average = AverageColor(SKRect.Inflate(rect, _unit * 0.1f, _unit * 0.05f));
            Shadow(rr, font.Size);
            using var fill = new SKPaint { IsAntialias = true, Color = WithValue(average, 30, 60) };
            _canvas.DrawRoundRect(rr, fill);
            Highlight(rr, font.Size, 55);
            fill.Color = SKColors.White;
            DrawClipped(font, fill, padX, rect, SKTextAlign.Left);
        }

        public void Glass(SKColor tint, SKColor ink, byte rim)
        {
            using var font = Font(bold: false, _unit * 0.062f);
            var rect = LabelRect(font, out var padX);
            var rr = new SKRoundRect(rect, rect.Height * 0.3f, rect.Height * 0.3f);
            Shadow(rr, font.Size);
            Frost(rr, font.Size, tint);
            Highlight(rr, font.Size, rim);
            using var paint = new SKPaint { IsAntialias = true, Color = ink };
            DrawClipped(font, paint, padX, rect, SKTextAlign.Left);
        }

        public void Mark()
        {
            var margin = _unit * 0.05f;
            using var font = Font(bold: false, _unit * 0.056f);
            var markH = font.Size * 1.25f;
            var x = margin;
            var mark = MarkBitmap.Value;
            if (mark is not null)
            {
                using var shadow = new SKPaint { IsAntialias = true, ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0, 0, 0, 150), SKBlendMode.SrcIn), ImageFilter = SKImageFilter.CreateBlur(font.Size * 0.3f, font.Size * 0.3f) };
                DrawMark(mark, x, margin, markH, shadow);
                x += MarkWidth(mark, markH) + (font.Size * 0.35f);
            }

            var baseline = Baseline(font, margin + (markH / 2));
            using var textShadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 160), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, font.Size * 0.3f) };
            _canvas.DrawText(_text, x, baseline + (font.Size * 0.05f), SKTextAlign.Left, font, textShadow);
            using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };
            _canvas.DrawText(_text, x, baseline, SKTextAlign.Left, font, ink);
        }

        public void Cinema()
        {
            var top = _h - (_h > _w ? _h * 0.26f : _h * 0.38f);
            var area = new SKRect(0, top, _w, _h);
            var deep = WithValue(AverageColor(area), 28, 65);
            using var snapshot = SKImage.FromBitmap(_bitmap);

            // Progressive blur: a blurred copy, faded in from the top of the area.
            _canvas.SaveLayer(area, null);
            using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(_unit * 0.03f, _unit * 0.03f) })
            {
                _canvas.DrawImage(snapshot, 0, 0, blur);
            }

            using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + ((_h - top) * 0.55f)), [new SKColor(0, 0, 0, 0), SKColors.Black], SKShaderTileMode.Clamp))
            using (var fade = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = shader })
            {
                _canvas.DrawRect(area, fade);
            }

            _canvas.Restore();
            using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, _h), [deep.WithAlpha(0), deep.WithAlpha(150), deep.WithAlpha(205)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp))
            using (var tint = new SKPaint { Shader = shader })
            {
                _canvas.DrawRect(area, tint);
            }

            using var font = Font(bold: false, _unit * 0.06f);
            var margin = _unit * 0.06f;
            var baseline = _h - margin;
            var x = margin;
            var mark = MarkBitmap.Value;
            if (mark is not null)
            {
                var markH = font.Size * 1.2f;
                DrawMark(mark, x, baseline - (font.Size * 0.36f) - (markH / 2), markH, null);
                x += MarkWidth(mark, markH) + (font.Size * 0.35f);
            }

            using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };
            _canvas.DrawText(_text, x, baseline, SKTextAlign.Left, font, ink);
        }

        // Diagonal sash across the top-left corner. Top left: apps put their own marks (watched, versions) top right.
        public void Ribbon(bool glass)
        {
            var reach = _unit * 0.38f;
            var thickness = _unit * 0.09f;
            var label = _text.ToUpper(CultureInfo.CurrentCulture);
            using var font = Font(bold: true, thickness * 0.48f);
            var visible = reach * MathF.Sqrt(2) * 0.76f;
            var measured = font.MeasureText(label);
            if (measured > visible)
            {
                font.Size *= visible / measured;
            }

            var band = new SKRect(-reach * 2, -thickness / 2, reach * 2, thickness / 2);
            using var snapshot = glass ? SKImage.FromBitmap(_bitmap) : null;
            _canvas.Save();
            _canvas.Translate(reach / 2, reach / 2);
            _canvas.RotateDegrees(-45);
            using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, thickness * 0.18f) })
            {
                _canvas.DrawRect(new SKRect(band.Left, band.Top + (thickness * 0.1f), band.Right, band.Bottom + (thickness * 0.1f)), shadow);
            }

            if (snapshot is not null)
            {
                _canvas.Save();
                _canvas.ClipRect(band, antialias: true);
                _canvas.RotateDegrees(45);
                _canvas.Translate(-reach / 2, -reach / 2);
                using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(thickness * 0.5f, thickness * 0.5f) })
                {
                    var area = new SKRect(0, 0, reach * 1.6f, reach * 1.6f);
                    _canvas.DrawImage(snapshot, area, area, new SKSamplingOptions(SKFilterMode.Linear), blur);
                }

                _canvas.Restore();
                using var tint = new SKPaint { IsAntialias = true, Color = Friend.WithAlpha(185) };
                _canvas.DrawRect(band, tint);
            }
            else
            {
                using var fill = new SKPaint { IsAntialias = true, Color = Friend };
                _canvas.DrawRect(band, fill);
            }

            Highlight(new SKRoundRect(band, 0, 0), thickness * 0.5f, 70);
            using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };
            _canvas.DrawText(label, 0, Baseline(font, 0), SKTextAlign.Center, font, ink);
            _canvas.Restore();
        }

        public SKRect BottomArea(float share) => new(0, _h - (_unit * share), _w, _h);

        // Average colour of an area (sampled), for styles that take their colour from the poster.
        public SKColor AverageColor(SKRect area)
        {
            area = SKRect.Intersect(area, new SKRect(0, 0, _w, _h));
            var pixels = _bitmap.GetPixelSpan();
            var (ri, bi) = ChannelOrder();
            long r = 0, g = 0, b = 0, n = 0;
            for (var y = (int)area.Top; y < (int)area.Bottom; y += 3)
            {
                for (var x = (int)area.Left; x < (int)area.Right; x += 3)
                {
                    var i = ((y * _w) + x) * 4;
                    r += pixels[i + ri];
                    g += pixels[i + 1];
                    b += pixels[i + bi];
                    n++;
                }
            }

            return n == 0 ? SKColors.Black : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }

        // The most vivid hue of the whole image (weighted by saturation and brightness).
        public SKColor VibrantColor()
        {
            var pixels = _bitmap.GetPixelSpan();
            var (ri, bi) = ChannelOrder();
            var weight = new double[12];
            var sums = new long[12, 4];
            for (var y = 0; y < _h; y += 4)
            {
                for (var x = 0; x < _w; x += 4)
                {
                    var i = ((y * _w) + x) * 4;
                    var c = new SKColor(pixels[i + ri], pixels[i + 1], pixels[i + bi]);
                    c.ToHsv(out var hue, out var sat, out var val);
                    if (sat < 30 || val < 25)
                    {
                        continue;
                    }

                    var bin = (int)(hue / 30) % 12;
                    weight[bin] += sat * val;
                    sums[bin, 0] += c.Red;
                    sums[bin, 1] += c.Green;
                    sums[bin, 2] += c.Blue;
                    sums[bin, 3]++;
                }
            }

            var best = Array.IndexOf(weight, weight.Max());
            var count = sums[best, 3];
            return count == 0 ? AverageColor(new SKRect(0, 0, _w, _h)) : new SKColor((byte)(sums[best, 0] / count), (byte)(sums[best, 1] / count), (byte)(sums[best, 2] / count));
        }

        // Byte offsets of red and blue in a pixel (images are decoded as RGBA; some platforms default to BGRA).
        private (int Red, int Blue) ChannelOrder() => _bitmap.ColorType == SKColorType.Bgra8888 ? (2, 0) : (0, 2);

        private SKFont Font(bool bold, float size) => new(PickTypeface(_text, bold), size) { Edging = SKFontEdging.Antialias, Subpixel = true };

        private SKRect LabelRect(SKFont font, out float padX)
        {
            padX = font.Size * 0.72f;
            var height = font.Size * 1.85f;
            var margin = _unit * 0.045f;
            var width = Math.Min(font.MeasureText(_text) + (2 * padX), _w - (2 * margin));
            return new SKRect(margin, _h - margin - height, margin + width, _h - margin);
        }

        private void DrawClipped(SKFont font, SKPaint paint, float padX, SKRect rect, SKTextAlign align)
        {
            _canvas.Save();
            _canvas.ClipRect(new SKRect(rect.Left + (padX / 2), rect.Top, rect.Right - (padX / 2), rect.Bottom));
            var x = align == SKTextAlign.Center ? rect.MidX : rect.Left + padX;
            _canvas.DrawText(_text, x, Baseline(font, rect.MidY), align, font, paint);
            _canvas.Restore();
        }

        private void Shadow(SKRoundRect rr, float size)
        {
            using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.45f) };
            _canvas.Save();
            _canvas.Translate(0, size * 0.14f);
            _canvas.DrawRoundRect(rr, shadow);
            _canvas.Restore();
        }

        // Light catching the top edge, as on glass.
        private void Highlight(SKRoundRect rr, float size, byte strokeAlpha)
        {
            var r = rr.Rect;
            using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.MidY), [new SKColor(255, 255, 255, 46), new SKColor(255, 255, 255, 0)], SKShaderTileMode.Clamp))
            using (var sheen = new SKPaint { IsAntialias = true, Shader = shader })
            {
                _canvas.DrawRoundRect(rr, sheen);
            }

            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, size * 0.045f), Color = new SKColor(255, 255, 255, strokeAlpha) };
            var inset = new SKRoundRect(rr);
            inset.Inflate(-stroke.StrokeWidth / 2, -stroke.StrokeWidth / 2);
            _canvas.DrawRoundRect(inset, stroke);
        }

        // The image blurred behind a shape, then tinted.
        private void Frost(SKRoundRect rr, float size, SKColor tint)
        {
            using var snapshot = SKImage.FromBitmap(_bitmap);
            _canvas.Save();
            _canvas.ClipRoundRect(rr, antialias: true);
            var around = SKRect.Inflate(rr.Rect, size * 2, size * 2);
            using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(size * 0.9f, size * 0.9f) })
            {
                _canvas.DrawImage(snapshot, around, around, new SKSamplingOptions(SKFilterMode.Linear), blur);
            }

            using var paint = new SKPaint { IsAntialias = true, Color = tint };
            _canvas.DrawRoundRect(rr, paint);
            _canvas.Restore();
        }

        private void DrawMark(SKBitmap mark, float x, float top, float height, SKPaint? shadow)
        {
            var dest = SKRect.Create(x, top, MarkWidth(mark, height), height);
            using var image = SKImage.FromBitmap(mark);
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            if (shadow is not null)
            {
                _canvas.DrawImage(image, SKRect.Create(dest.Left, dest.Top + (height * 0.04f), dest.Width, dest.Height), sampling, shadow);
            }

            _canvas.DrawImage(image, dest, sampling);
        }

        private static float MarkWidth(SKBitmap mark, float height) => height * mark.Width / mark.Height;

        private static float Baseline(SKFont font, float midY)
        {
            font.GetFontMetrics(out var metrics);
            return midY + (metrics.CapHeight / 2);
        }

        private static SKRoundRect Corners(SKRect rect, float topLeft, float topRight, float bottomRight, float bottomLeft)
        {
            var rr = new SKRoundRect();
            rr.SetRectRadii(rect, [new SKPoint(topLeft, topLeft), new SKPoint(topRight, topRight), new SKPoint(bottomRight, bottomRight), new SKPoint(bottomLeft, bottomLeft)]);
            return rr;
        }

        // Inter for Latin, Greek and Cyrillic; names in other scripts (CJK, Arabic...) get a system font that has them.
        private static SKTypeface PickTypeface(string text, bool bold)
        {
            var typeface = bold ? Bold.Value : SemiBold.Value;
            var missing = text.EnumerateRunes().FirstOrDefault(r => !Rune.IsWhiteSpace(r) && !typeface.ContainsGlyph(r.Value));
            if (missing.Value == 0)
            {
                return typeface;
            }

            return SKFontManager.Default.MatchCharacter(null, bold ? SKFontStyle.Bold : SKFontStyle.Normal, null, missing.Value) ?? typeface;
        }
    }
}
