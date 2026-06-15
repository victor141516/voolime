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
    using var image = DrawVoolimeIcon(size);
    frames.Add((size, EncodePng(image)));
}

WriteIco(iconPath, frames);
using (var preview = DrawVoolimeIcon(512))
{
    preview.Save(previewPath, ImageFormat.Png);
}

Console.WriteLine(iconPath);
Console.WriteLine(previewPath);

static Bitmap DrawVoolimeIcon(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.Transparent);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.ScaleTransform(size / 256f, size / 256f);

    var speakerBounds = Rect(25, 47, 160, 160);
    var pulpBounds = Rect(45, 67, 120, 120);
    var center = new PointF(105, 127);

    using var shadow = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
    graphics.FillEllipse(shadow, 38, 192, 136, 18);

    using var outerBrush = new LinearGradientBrush(speakerBounds, FromHtml("#58D34A"), FromHtml("#14723B"), 115f);
    using var outerPen = new Pen(FromHtml("#0E5132"), 8f) { LineJoin = LineJoin.Round };
    graphics.FillEllipse(outerBrush, speakerBounds);
    graphics.DrawEllipse(outerPen, speakerBounds);

    using var rimPen = new Pen(Color.FromArgb(230, 228, 255, 175), 7f);
    graphics.DrawEllipse(rimPen, Rect(39, 61, 132, 132));

    using var pulpClip = new GraphicsPath();
    pulpClip.AddEllipse(pulpBounds);
    var previousClip = graphics.Clip;
    graphics.SetClip(pulpClip);

    var segmentColors = new[]
    {
        FromHtml("#DFFF67"),
        FromHtml("#B9F143"),
        FromHtml("#91DB35"),
        FromHtml("#CFFB55")
    };
    for (var i = 0; i < 10; i++)
    {
        using var segmentBrush = new SolidBrush(segmentColors[i % segmentColors.Length]);
        graphics.FillPie(segmentBrush, pulpBounds, -90 + i * 36, 36);
    }

    graphics.Clip = previousClip;

    using var pulpPen = new Pen(Color.FromArgb(230, 247, 255, 203), 5f);
    graphics.DrawEllipse(pulpPen, pulpBounds);

    using var segmentPen = new Pen(Color.FromArgb(160, 42, 118, 43), 4f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    };
    for (var i = 0; i < 10; i++)
    {
        var angle = -90 + i * 36;
        var radians = Math.PI * angle / 180d;
        var end = new PointF(
            center.X + (float)Math.Cos(radians) * 59f,
            center.Y + (float)Math.Sin(radians) * 59f);
        graphics.DrawLine(segmentPen, center, end);
    }

    using var coneBrush = new PathGradientBrush(pulpClip)
    {
        CenterColor = Color.FromArgb(95, 8, 34, 26),
        SurroundColors = [Color.FromArgb(0, 8, 34, 26)]
    };
    graphics.FillEllipse(coneBrush, pulpBounds);

    using var capBrush = new LinearGradientBrush(Rect(83, 105, 44, 44), FromHtml("#1D4836"), FromHtml("#0B2119"), 90f);
    using var capPen = new Pen(Color.FromArgb(225, 229, 255, 188), 4f);
    graphics.FillEllipse(capBrush, Rect(83, 105, 44, 44));
    graphics.DrawEllipse(capPen, Rect(83, 105, 44, 44));

    using var glint = new SolidBrush(Color.FromArgb(145, 255, 255, 255));
    graphics.FillEllipse(glint, 66, 72, 35, 19);

    return bitmap;
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
