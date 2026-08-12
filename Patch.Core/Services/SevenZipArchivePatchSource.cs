using SevenZip;

namespace Patch.Core.Services;

public sealed class SevenZipArchivePatchSource : IArchivePatchSource
{
    private readonly string _tempDir;
    private readonly Dictionary<string, string> _diskPaths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> EntryPaths { get; }

    public bool SupportsCheapRepeatedOpen => true;

    public SevenZipArchivePatchSource(string path)
    {
        NativeSevenZip.EnsureInitialized();

        _tempDir = Path.Combine(Path.GetDirectoryName(path)!, "romforge_7z_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        using (var extractor = new SevenZipExtractor(path))
        {
            extractor.ExtractArchive(_tempDir);
        }

        foreach (var filePath in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_tempDir, filePath).Replace('\\', '/');
            _diskPaths[relativePath] = filePath;
        }

        EntryPaths = [.. _diskPaths.Keys];
    }

    public IArchivePatchEntry? FindEntry(string path)
    {
        if (!_diskPaths.TryGetValue(path, out var diskPath))
            return null;

        return new Entry(diskPath, path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private sealed class Entry(string diskPath, string fullPath) : IArchivePatchEntry
    {
        public string FullPath => fullPath;

        public long Length => new FileInfo(diskPath).Length;

        public Stream Open()
        {
            return new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }
}