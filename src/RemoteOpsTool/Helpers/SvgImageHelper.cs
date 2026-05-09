using SkiaSharp;
using Svg.Skia;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteOpsTool.Helpers;

public static class SvgImageHelper
{
    public static ImageSource LoadSvg(string svgPath, int width, int height)
    {
        using var fileStream = File.OpenRead(svgPath);
        return LoadSvg(fileStream, width, height);
    }

    public static ImageSource LoadSvg(Stream stream, int width, int height)
    {
        try
        {
            var svg = new SKSvg();
            svg.Load(stream);

            if (svg.Picture == null)
                return null!;

            var scaleX = (float)width / svg.Picture.CullRect.Width;
            var scaleY = (float)height / svg.Picture.CullRect.Height;
            var scale = Math.Min(scaleX, scaleY);

            var info = new SKImageInfo(width, height);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.DrawPicture(svg.Picture);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var pngBytes = data.ToArray();

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(pngBytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null!;
        }
    }
}
