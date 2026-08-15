using SevenZip;

namespace Patch.Core.Services;

public sealed class SevenZipArchivePatchSource : IArchivePatchSource
{
    private readonly string _tempDir;
    private readonly Dictionary<string, string> _diskPaths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> EntryPaths { get; }

    public bool SupportsCheapRepeatedOpen => true;

    public SevenZipArchivePatchSource(string path, string? password = null)
    {
        NativeSevenZip.EnsureInitialized();

        _tempDir = Path.Combine(Path.GetDirectoryName(path)!, "romforge_7z_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        try
        {
            using var extractor = string.IsNullOrEmpty(password) ? new SevenZipExtractor(path) : new SevenZipExtractor(path, password);
            extractor.ExtractArchive(_tempDir);
        }
        catch (Exception)
        {
            SafeDeleteTempDir();
            throw new ArchivePasswordRequiredException(path);
        }

        foreach (var filePath in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_tempDir, filePath).Replace('\\', '/');
            _diskPaths[relativePath] = filePath;
        }

        if (_diskPaths.Count == 0)
        {
            SafeDeleteTempDir();
            throw new InvalidOperationException("압축 파일에 항목이 없습니다.");
        }

        EntryPaths = [.. _diskPaths.Keys];
    }

    private void SafeDeleteTempDir()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
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
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private sealed class Entry(string diskPath, string fullPath) : IArchivePatchEntry
    {
        public string FullPath => fullPath;

        public long Length => new FileInfo(diskPath).Length;

        public Stream Open() => new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}