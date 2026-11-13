using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CrabDesk.Native;

public enum FlatFileIconKind
{
    Generic,
    Text,
    Pdf,
    Document,
    Spreadsheet,
    Presentation,
    Archive,
    Image,
    Audio,
    Video,
    Code
}

internal sealed record FlatFileIconStyle(FlatFileIconKind Kind, string Label, Color Accent)
{
    internal static FlatFileIconStyle FromPath(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        var kind = extension switch
        {
            ".pdf" => FlatFileIconKind.Pdf,
            ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".odt" or ".rtf" => FlatFileIconKind.Document,
            ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".ods" or ".csv" or ".tsv" => FlatFileIconKind.Spreadsheet,
            ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".odp" => FlatFileIconKind.Presentation,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" or ".cab" or ".iso" => FlatFileIconKind.Archive,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".ico" or ".tif" or ".tiff" or
                ".jfif" or ".heic" or ".heif" or ".avif" or ".svg" => FlatFileIconKind.Image,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".wma" or ".aiff" => FlatFileIconKind.Audio,
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".wmv" or ".m4v" or ".mpg" or ".mpeg" => FlatFileIconKind.Video,
            ".cs" or ".js" or ".jsx" or ".ts" or ".tsx" or ".py" or ".rs" or ".go" or ".c" or ".cpp" or
                ".h" or ".java" or ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".html" or
                ".css" or ".sql" or ".sh" or ".ps1" or ".bat" or ".cmd" => FlatFileIconKind.Code,
            ".txt" or ".md" or ".markdown" or ".log" or ".rst" => FlatFileIconKind.Text,
            _ => FlatFileIconKind.Generic
        };
        var accent = kind switch
        {
            FlatFileIconKind.Pdf => Color.FromArgb(211, 56, 65),
            FlatFileIconKind.Document => Color.FromArgb(43, 102, 199),
            FlatFileIconKind.Spreadsheet => Color.FromArgb(33, 133, 84),
            FlatFileIconKind.Presentation => Color.FromArgb(207, 88, 50),
            FlatFileIconKind.Archive => Color.FromArgb(137, 93, 174),
            FlatFileIconKind.Image => Color.FromArgb(33, 135, 161),
            FlatFileIconKind.Audio => Color.FromArgb(192, 62, 123),
            FlatFileIconKind.Video => Color.FromArgb(98, 88, 190),
            FlatFileIconKind.Code => Color.FromArgb(42, 116, 123),
            FlatFileIconKind.Text => Color.FromArgb(98, 113, 132),
            _ => Color.FromArgb(111, 119, 130)
        };
        var label = extension.Length is >= 2 and <= 5 && extension.AsSpan(1).ContainsAnyExcept(
            "abcdefghijklmnopqrstuvwxyz0123456789".AsSpan()) == false
            ? extension[1..].ToUpperInvariant()
            : kind == FlatFileIconKind.Text ? "TEXT" : "FILE";
        return new(kind, label, accent);
    }
}

public static partial class FlatIconSkin
{
    private static Bitmap RenderFile(Bitmap source, int size, IconSkinOptions options, FlatFileIconStyle style)
    {
        var work = size <= SupersampleBelow ? size * 2 : size;
        using var layer = new Bitmap(work, work, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(layer))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.ScaleTransform(work, work);

            const float left = 0.16f, right = 0.84f, top = 0.045f, bottom = 0.955f, fold = 0.175f;
            using var paper = new GraphicsPath();
            paper.AddLine(left + 0.035f, top, right - fold, top);
            paper.AddLine(right - fold, top, right, top + fold);
            paper.AddLine(right, top + fold, right, bottom - 0.035f);
            paper.AddArc(right - 0.07f, bottom - 0.07f, 0.07f, 0.07f, 0, 90);
            paper.AddLine(right - 0.035f, bottom, left + 0.035f, bottom);
            paper.AddArc(left, bottom - 0.07f, 0.07f, 0.07f, 90, 90);
            paper.AddLine(left, bottom - 0.035f, left, top + 0.035f);
            paper.AddArc(left, top, 0.07f, 0.07f, 180, 90);
            paper.CloseFigure();
            using var body = new SolidBrush(options.FolderPlate);
            graphics.FillPath(body, paper);

            var darkPaper = Luminance(options.FolderPlate) < 0.45;
            using var outline = new Pen(Color.FromArgb(darkPaper ? 100 : 55, darkPaper ? Color.White : Color.Black), 0.012f);
            graphics.DrawPath(outline, paper);
            using var foldBrush = new SolidBrush(Color.FromArgb(darkPaper ? 40 : 18, darkPaper ? Color.White : Color.Black));
            PointF[] foldPoints = [new(right - fold, top), new(right - fold, top + fold), new(right, top + fold)];
            graphics.FillPolygon(foldBrush, foldPoints);
            graphics.DrawLines(outline, foldPoints);

            var state = graphics.Save();
            graphics.SetClip(paper);
            using var accent = new SolidBrush(style.Accent);
            graphics.FillRectangle(accent, left, 0.69f, right - left, bottom - 0.69f);
            graphics.Restore(state);

            var contentColor = darkPaper ? Color.FromArgb(229, 235, 244) : style.Accent;
            DrawFileContent(graphics, source, style.Kind, contentColor);
            if (size >= 24)
            {
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                var fontSize = 0.17f;
                using var initialFont = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                var measuredWidth = graphics.MeasureString(style.Label, initialFont, new PointF(), format).Width;
                if (measuredWidth > 0.55f) fontSize *= 0.55f / measuredWidth;
                using var font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                graphics.DrawString(style.Label, font, Brushes.White, new RectangleF(left + 0.025f, 0.70f, right - left - 0.05f, 0.24f), format);
            }
        }
        var rgba = ReadRgba(layer);
        return work == size ? ToBitmap(rgba, work) : DownsampleTile(rgba, work);
    }

    private static void DrawFileContent(Graphics graphics, Bitmap source, FlatFileIconKind kind, Color ink)
    {
        using var pen = new Pen(ink, 0.023f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);
        switch (kind)
        {
            case FlatFileIconKind.Image:
            case FlatFileIconKind.Generic:
            {
                var bounds = new RectangleF(0.25f, 0.26f, 0.50f, 0.35f);
                var scale = Math.Min(bounds.Width / source.Width, bounds.Height / source.Height);
                var width = source.Width * scale;
                var height = source.Height * scale;
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                var x = bounds.X + (bounds.Width - width) / 2;
                var y = bounds.Y + (bounds.Height - height) / 2;
                PointF[] target = [new(x, y), new(x + width, y), new(x, y + height)];
                graphics.DrawImage(source, target, new RectangleF(0, 0, source.Width, source.Height), GraphicsUnit.Pixel, attributes);
                break;
            }
            case FlatFileIconKind.Spreadsheet:
                graphics.DrawRectangle(pen, 0.28f, 0.29f, 0.44f, 0.28f);
                graphics.DrawLine(pen, 0.28f, 0.38f, 0.72f, 0.38f);
                graphics.DrawLine(pen, 0.28f, 0.48f, 0.72f, 0.48f);
                graphics.DrawLine(pen, 0.43f, 0.29f, 0.43f, 0.57f);
                graphics.DrawLine(pen, 0.57f, 0.29f, 0.57f, 0.57f);
                break;
            case FlatFileIconKind.Presentation:
                graphics.DrawRectangle(pen, 0.28f, 0.28f, 0.44f, 0.28f);
                graphics.FillRectangle(brush, 0.35f, 0.43f, 0.065f, 0.07f);
                graphics.FillRectangle(brush, 0.47f, 0.37f, 0.065f, 0.13f);
                graphics.FillRectangle(brush, 0.59f, 0.33f, 0.065f, 0.17f);
                break;
            case FlatFileIconKind.Archive:
                for (var row = 0; row < 6; row++)
                    graphics.FillRectangle(brush, row % 2 == 0 ? 0.44f : 0.50f, 0.24f + row * 0.045f, 0.06f, 0.035f);
                graphics.DrawRectangle(pen, 0.445f, 0.53f, 0.11f, 0.07f);
                break;
            case FlatFileIconKind.Audio:
                graphics.FillEllipse(brush, 0.30f, 0.47f, 0.12f, 0.09f);
                graphics.FillEllipse(brush, 0.56f, 0.42f, 0.12f, 0.09f);
                graphics.DrawLines(pen, [new PointF(0.41f, 0.51f), new(0.41f, 0.29f), new(0.67f, 0.24f), new(0.67f, 0.46f)]);
                break;
            case FlatFileIconKind.Video:
                graphics.FillPolygon(brush, [new PointF(0.40f, 0.27f), new(0.40f, 0.58f), new(0.66f, 0.425f)]);
                break;
            case FlatFileIconKind.Code:
                graphics.DrawLines(pen, [new PointF(0.38f, 0.33f), new(0.28f, 0.43f), new(0.38f, 0.53f)]);
                graphics.DrawLine(pen, 0.55f, 0.29f, 0.46f, 0.57f);
                graphics.DrawLines(pen, [new PointF(0.63f, 0.33f), new(0.73f, 0.43f), new(0.63f, 0.53f)]);
                break;
            default:
                graphics.DrawLine(pen, 0.29f, 0.31f, 0.61f, 0.31f);
                graphics.DrawLine(pen, 0.29f, 0.415f, 0.71f, 0.415f);
                graphics.DrawLine(pen, 0.29f, 0.52f, 0.56f, 0.52f);
                break;
        }
    }
}
