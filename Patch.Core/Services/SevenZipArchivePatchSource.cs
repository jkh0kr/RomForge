using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

namespace Patch.Core.Services;

public sealed class SevenZipArchivePatchSource : IArchivePatchSource
{
    private readonly IArchive _archive;
    private readonly Dictionary<string, IArchiveEntry> _byPath;

    public IReadOnlyList<string> EntryPaths { get; }

    public bool SupportsCheapRepeatedOpen => false;

    public SevenZipArchivePatchSource(string path)
    {
        _archive = SevenZipArchive.OpenArchive(path);
        _byPath = new Dictionary<string, IArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _archive.Entries)
        {
            if (entry.IsDirectory || entry.Key is null)
                continue;

            _byPath[entry.Key.Replace('\\', '/')] = entry;
        }

        EntryPaths = [.. _byPath.Keys];
    }

    public IArchivePatchEntry? FindEntry(string path) => _byPath.TryGetValue(path, out var entry) ? new Entry(entry) : null;

    public void Dispose() => _archive.Dispose();

    private sealed class Entry(IArchiveEntry entry) : IArchivePatchEntry
    {
        public string FullPath => entry.Key!.Replace('\\', '/');

        public long Length => entry.Size;

        public Stream Open() => entry.OpenEntryStream();
    }
}