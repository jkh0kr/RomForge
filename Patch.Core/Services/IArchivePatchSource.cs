namespace Patch.Core.Services;

public interface IArchivePatchSource : IDisposable
{
    IReadOnlyList<string> EntryPaths { get; }

    IArchivePatchEntry? FindEntry(string path);

    bool SupportsCheapRepeatedOpen { get; }
}

public interface IArchivePatchEntry
{
    string FullPath { get; }

    long Length { get; }

    Stream Open();
}