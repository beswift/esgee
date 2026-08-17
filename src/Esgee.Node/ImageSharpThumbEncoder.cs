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
        // AutoOrient BEFORE the resize: a phone JPEG's EXIF Orientation must
        // rotate the pixels, not ride along as metadata. SkipMetadata then
        // keeps the tag (and everything else) out of the re-encoded thumb —
        // matching the WPF encoder, which emits no metadata at all. Without
        // both, the same shared item rendered by an EXIF-honoring client
        // (the Mac app's NSImage, any browser) disagrees in orientation with
        // a Windows-encoded thumb of the same capture.
        image.Mutate(x => x.AutoOrient().Resize(448, 0)); // height 0 = preserve aspect
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 80, SkipMetadata = true });
        return ms.ToArray();
    }
}
