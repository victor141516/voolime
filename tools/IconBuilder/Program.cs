using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var root = FindRepoRoot();
var assets = Path.Combine(root, "assets");
Directory.CreateDirectory(assets);

var iconPath = Path.Combine(assets, "voolime.ico");
var previewPath = Path.Combine(assets, "voolime-preview.png");

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var frames = new List<(int Size, byte[] Png)>();
foreach (var size in sizes)
{
    using var image = DrawBoLime(size);
    frames.Add((size, EncodePng(image)));
}

WriteIco(iconPath, frames);
using (var preview = DrawBoLime(512))
{
    preview.Save(previewPath, ImageFormat.Png);
}

Console.WriteLine(iconPath);
Console.WriteLine(previewPath);

static Bitmap DrawBoLime(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.Transparent);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.ScaleTransform(size / 256f, size / 256f);

    using var shadow = new SolidBrush(Color.FromArgb(48, 0, 0, 0));
    graphics.FillEllipse(shadow, 38, 174, 144, 18);

    var limeOuter = Rect(34, 39, 154, 154);
    using var outerBrush = new LinearGradientBrush(limeOuter, FromHtml("#56C83F"), FromHtml("#208E45"), 110f);
    using var outerPen = new Pen(FromHtml("#125C34"), 7f) { LineJoin = LineJoin.Round };
    graphics.FillEllipse(outerBrush, limeOuter);
    graphics.DrawEllipse(outerPen, limeOuter);

    var limeInner = Rect(52, 57, 118, 118);
    using var innerBrush = new LinearGradientBrush(limeInner, FromHtml("#E4FF79"), FromHtml("#91DF39"), 90f);
    using var innerPen = new Pen(Color.FromArgb(210, 255, 255, 232), 5f);
    graphics.FillEllipse(innerBrush, limeInner);
    graphics.DrawEllipse(innerPen, limeInner);

    using var segmentPen = new Pen(Color.FromArgb(145, 32, 111, 42), 4f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    };
    var center = new PointF(111, 116);
    foreach (var angle in new[] { -70, -25, 20, 65, 110, 155 })
    {
        var radians = Math.PI * angle / 180d;
        var end = new PointF(
            center.X + (float)Math.Cos(radians) * 55f,
            center.Y + (float)Math.Sin(radians) * 55f);
        graphics.DrawLine(segmentPen, center, end);
    }

    using var glint = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
    graphics.FillEllipse(glint, 73, 68, 34, 18);

    using var bodyBrush = new LinearGradientBrush(Rect(121, 86, 82, 91), FromHtml("#263442"), FromHtml("#111923"), 0f);
    using var bodyPen = new Pen(Color.FromArgb(235, 9, 16, 25), 6f) { LineJoin = LineJoin.Round };
    using var speakerPath = new GraphicsPath();
    speakerPath.AddPolygon(new[]
    {
        new PointF(121, 108),
        new PointF(148, 108),
        new PointF(190, 76),
        new PointF(190, 180),
        new PointF(148, 148),
        new PointF(121, 148)
    });
    graphics.FillPath(bodyBrush, speakerPath);
    graphics.DrawPath(bodyPen, speakerPath);

    using var grilleBrush = new SolidBrush(Color.FromArgb(110, 255, 255, 255));
    graphics.FillRoundedRectangle(grilleBrush, Rect(132, 118, 18, 20), 5);

    DrawWave(graphics, Rect(177, 94, 42, 68), 9f);
    DrawWave(graphics, Rect(163, 76, 73, 104), 9f);

    return bitmap;
}

static void DrawWave(Graphics graphics, RectangleF rect, float width)
{
    using var outline = new Pen(Color.FromArgb(235, 11, 19, 28), width + 5f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    };
    using var wave = new Pen(FromHtml("#DFFFF0"), width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    };
    graphics.DrawArc(outline, rect, -42, 84);
    graphics.DrawArc(wave, rect, -42, 84);
}

static byte[] EncodePng(Bitmap bitmap)
{
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> frames)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Count);

    var offset = 6 + frames.Count * 16;
    foreach (var frame in frames)
    {
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)frame.Png.Length);
        writer.Write((uint)offset);
        offset += frame.Png.Length;
    }

    foreach (var frame in frames)
    {
        writer.Write(frame.Png);
    }
}

static RectangleF Rect(float x, float y, float width, float height) => new(x, y, width, height);

static Color FromHtml(string value) => ColorTranslator.FromHtml(value);

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Voolime.csproj")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2f;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
