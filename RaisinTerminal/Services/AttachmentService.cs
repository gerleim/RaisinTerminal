using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RaisinTerminal.Services;

public static class AttachmentService
{
    private static string AttachmentsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RaisinTerminal", "attachments");

    public static string GetProjectAttachmentsDir(string projectId)
    {
        return Path.Combine(AttachmentsRoot, projectId);
    }

    public static string SaveImage(BitmapSource image, string projectId)
    {
        var dir = GetProjectAttachmentsDir(projectId);
        Directory.CreateDirectory(dir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"img_{timestamp}.png";
        var filePath = Path.Combine(dir, fileName);

        // WPF Clipboard.GetImage() often returns images with a zeroed-out alpha channel,
        // which produces a fully transparent (blank) PNG. Fix by forcing alpha to 255.
        var fixedImage = FixClipboardAlpha(image);

        using var stream = File.Create(filePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(fixedImage));
        encoder.Save(stream);

        return filePath;
    }

    private static BitmapSource FixClipboardAlpha(BitmapSource source)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        // Set every alpha byte to fully opaque
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public static List<string> GetAttachments(string projectId)
    {
        var dir = GetProjectAttachmentsDir(projectId);
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*.png")
            .OrderByDescending(File.GetCreationTime)
            .ToList();
    }

    public static string? RenameAttachment(string filePath, string newFileName)
    {
        if (!File.Exists(filePath)) return null;
        var dir = Path.GetDirectoryName(filePath)!;
        var newPath = Path.Combine(dir, newFileName);
        if (File.Exists(newPath)) return null;
        File.Move(filePath, newPath);
        return newPath;
    }

    public static void DeleteAttachment(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
}
