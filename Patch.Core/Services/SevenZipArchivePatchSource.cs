using SevenZip;

namespace Patch.Core.Services;

public sealed class SevenZipArchivePatchSource : IArchivePatchSource
{
    private readonly SevenZipExtractor _extractor;
    private readonly Dictionary<string, ArchiveFileInfo> _byPath;

    public IReadOnlyList<string> EntryPaths { get; }

    public bool SupportsCheapRepeatedOpen => false;

    public SevenZipArchivePatchSource(string path)
    {
        NativeSevenZip.EnsureInitialized();

        _extractor = new SevenZipExtractor(path);
        _byPath = new Dictionary<string, ArchiveFileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in _extractor.ArchiveFileData)
        {
            if (info.IsDirectory)
                continue;

            _byPath[info.FileName.Replace('\\', '/')] = info;
        }

        EntryPaths = [.. _byPath.Keys];
    }

    public IArchivePatchEntry? FindEntry(string path) => _byPath.TryGetValue(path, out var info) ? new Entry(_extractor, info) : null;

    public void Dispose() => _extractor.Dispose();

    private sealed class Entry(SevenZipExtractor extractor, ArchiveFileInfo info) : IArchivePatchEntry
    {
        public string FullPath => info.FileName.Replace('\\', '/');

        public long Length => (long)info.Size;

        public Stream Open()
        {
            var stream = new MemoryStream();

            extractor.ExtractFile(info.Index, stream);
            stream.Position = 0;

            return stream;
        }
    }
}