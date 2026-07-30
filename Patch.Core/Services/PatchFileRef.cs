namespace Patch.Core.Services;

public sealed class PatchFileRef
{
    public string? DiskPath { get; }
    private readonly Func<Stream>? _zipStreamOpener;
    private readonly long _zipLength;

    private PatchFileRef(string? diskPath, Func<Stream>? zipStreamOpener, long zipLength)
    {
        DiskPath = diskPath;
        _zipStreamOpener = zipStreamOpener;
        _zipLength = zipLength;
    }

    public static PatchFileRef FromDisk(string path) => new(path, null, 0);

    public static PatchFileRef FromZip(Func<Stream> zipStreamOpener, long length) => new(null, zipStreamOpener, length);

    public bool IsDisk => DiskPath != null;

    public string DisplayName => DiskPath != null ? Path.GetFileName(DiskPath) : "(zip)";

    public long Length => IsDisk ? new FileInfo(DiskPath!).Length : _zipLength;

    public Stream OpenRead() => IsDisk ? File.OpenRead(DiskPath!) : _zipStreamOpener!();

    public byte[] ReadSmallFileBytes()
    {
        using var s = OpenRead();
        using var ms = new MemoryStream();

        s.CopyTo(ms);

        return ms.ToArray();
    }

    public async Task<byte[]> ReadSmallFileBytesAsync(CancellationToken ct = default)
    {
        await using var s = OpenRead();
        using var ms = new MemoryStream();

        await s.CopyToAsync(ms, ct);

        return ms.ToArray();
    }

    public MaterializedTempFile MaterializeAsTempFile(string suggestedExtension)
    {
        if (IsDisk)
            return MaterializedTempFile.Wrap(DiskPath!);

        string tempPath = Path.Combine(Path.GetTempPath(), $"RomForgePatch_{Guid.NewGuid():N}{suggestedExtension}");

        using (var dst = File.Create(tempPath))
        using (var src = OpenRead())
            src.CopyTo(dst);

        return MaterializedTempFile.Owned(tempPath);
    }
}

public readonly struct MaterializedTempFile(string path, bool owned) : IDisposable
{
    public string Path { get; } = path;

    public static MaterializedTempFile Wrap(string path) => new(path, false);

    public static MaterializedTempFile Owned(string path) => new(path, true);

    public void Dispose()
    {
        if (owned && File.Exists(Path))
        {
            try { File.Delete(Path); } catch { }
        }
    }
}