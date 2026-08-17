using System.IO;
using System.Windows.Media.Imaging;
using Esgee.Peers;

namespace Esgee.Interop;

/// <summary>WPF's side of the IThumbEncoder seam — the same scaled-down
/// BitmapImage decode the peer server always used (never the full bitmap),
/// run on connection worker threads and frozen so nothing ever touches the
/// dispatcher. The headless node's ImageSharp encoder is the other side.</summary>
internal sealed class WpfThumbEncoder : IThumbEncoder
{
    public byte[] EncodeThumb(string sourcePath)
    {
        using var fs = File.OpenRead(sourcePath);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = fs;
        bmp.DecodePixelWidth = 448;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
