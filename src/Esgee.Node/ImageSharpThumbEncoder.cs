using Esgee.Peers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Esgee.Node;

/// <summary>ImageSharp's side of the IThumbEncoder seam — same contract as the
/// app's WPF encoder: 448px-wide JPEG, aspect preserved, quality 80.</summary>
internal sealed class ImageSharpThumbEncoder : IThumbEncoder
{
    public byte[] EncodeThumb(string sourcePath)
    {
        using var image = Image.Load(sourcePath);
        image.Mutate(x => x.Resize(448, 0)); // height 0 = preserve aspect
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 80 });
        return ms.ToArray();
    }
}
