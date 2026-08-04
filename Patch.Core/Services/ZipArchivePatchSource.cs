using System.IO.Compression;

namespace Patch.Core.Services;

public sealed class ZipArchivePatchSource : IArchivePatchSource
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _byPath;

    public IReadOnlyList<string> EntryPaths { get; }

    public bool SupportsCheapRepeatedOpen => true;

    public ZipArchivePatchSource(string path)
    {
        _archive = ZipFile.OpenRead(path);
        _byPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            _byPath[entry.FullName.Replace('\\', '/')] = entry;
        }

        EntryPaths = [.. _byPath.Keys];
    }

    public IArchivePatchEntry? FindEntry(string path) => _byPath.TryGetValue(path, out var entry) ? new Entry(entry) : null;

    public void Dispose() => _archive.Dispose();

    private sealed class Entry(ZipArchiveEntry entry) : IArchivePatchEntry
    {
        public string FullPath => entry.FullName.Replace('\\', '/');

        public long Length => entry.Length;

        public Stream Open() => new ZipEntryLengthStream(entry);
    }
}