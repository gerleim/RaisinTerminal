namespace RaisinTerminal.Core.Images;

/// <summary>
/// Persists images to disk and manages thumbnails for a session.
/// </summary>
public class ImageStore
{
    private readonly string _baseDirectory;

    public ImageStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        Directory.CreateDirectory(_baseDirectory);
    }

    public string SaveImage(byte[] imageData, string extension = ".png")
    {
        var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}{extension}";
        var path = Path.Combine(_baseDirectory, fileName);
        File.WriteAllBytes(path, imageData);
        return path;
    }

    public IEnumerable<string> GetAllImages()
    {
        if (!Directory.Exists(_baseDirectory))
            return [];
        return Directory.GetFiles(_baseDirectory, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f);
    }

    public void DeleteImage(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
