namespace RaisinTerminal.Core.Images;

/// <summary>
/// Abstraction for clipboard image detection and extraction.
/// Concrete implementation lives in the WPF project since clipboard APIs require WPF references.
/// </summary>
public interface IClipboardImageHandler
{
    /// <summary>Returns true if the clipboard currently contains image data.</summary>
    bool HasImage();

    /// <summary>Gets the clipboard image as a PNG byte array, or null if none.</summary>
    byte[]? GetImageAsPng();
}
