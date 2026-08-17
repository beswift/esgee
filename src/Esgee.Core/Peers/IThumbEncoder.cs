namespace Esgee.Peers;

/// <summary>The one platform seam in the peer server: /thumb re-encodes a
/// capture as a small JPEG for grid tiles. The WPF app supplies BitmapImage;
/// the headless node supplies ImageSharp. Implementations are called on
/// connection worker threads — they must be thread-safe and must never
/// touch a UI thread.</summary>
public interface IThumbEncoder
{
    /// <summary>A 448px-wide JPEG (quality ~80, aspect preserved) of the image
    /// at <paramref name="sourcePath"/>. Throws on undecodable input — the
    /// server answers 500 for that one request.</summary>
    byte[] EncodeThumb(string sourcePath);
}
