using _3DS.Core.Interfaces;
using Common;
using Patch.Core;
using Patch.Core.Services;
using System.IO.Compression;

namespace _3DS.Core.Services;

public class PatchFolderFileSource : IRomFsFileSource
{
    private static readonly string[] PatchExtensions = [".xdelta", ".ips", ".bps", ".ups", ".ppf", ".aps"];

    private readonly string? _diskFolder;
    private readonly ZipArchive? _zipArchive;
    private readonly string _zipPrefix;

    private Dictionary<string, PatchFileRef>? _patchIndex; // key: 원본파일명 + 확장자, 디렉터리 무시(기존 동작 유지)
    private readonly Dictionary<string, byte[]> _resultCache = new(StringComparer.OrdinalIgnoreCase);
        
    public int AppliedCount { get; private set; }

    private PatchFolderFileSource(string? diskFolder, ZipArchive? zipArchive, string zipPrefix)
    {
        _diskFolder = diskFolder;
        _zipArchive = zipArchive;
        _zipPrefix = zipPrefix;
    }

    public static PatchFolderFileSource ForFolder(string patchFolder) => new(patchFolder, null, "");

    public static PatchFolderFileSource ForZip(ZipArchive archive, string prefix) => new(null, archive, prefix);

    public async ValueTask<Stream?> OpenFileAsync(string fullPath, Func<CancellationToken, ValueTask<Stream?>>? getOriginal = null, Action<string, LogLevel>? log = null, CancellationToken ct = default)
    {
        string relative = fullPath.TrimStart('/').Replace('\\', '/');
        Stream? direct = OpenDirect(relative);

        if (direct != null)
        {
            AppliedCount++;

            return direct;
        }

        if (_resultCache.TryGetValue(fullPath, out byte[]? cachedResult))
            return new MemoryStream(cachedResult);

        string targetFileName = Path.GetFileName(relative);
        var index = GetOrBuildPatchIndex();

        foreach (string ext in PatchExtensions)
        {
            string patchKey = targetFileName + ext;

            if (!index.TryGetValue(patchKey, out var patchRef))
                continue;

            if (getOriginal == null)
                throw new InvalidOperationException($"패치 파일을 적용하려면 원본 데이터가 필요하지만 제공되지 않았습니다: {relative}");

            var originalStream = await getOriginal(ct) ?? throw new FileNotFoundException($"원본 파일을 찾을 수 없어 패치를 적용할 수 없습니다: {relative}");
            byte[] originalData;

            await using (originalStream)
            {
                using var ms = new MemoryStream();

                await originalStream.CopyToAsync(ms, ct);
                originalData = ms.ToArray();
            }

            byte[] patchData = await patchRef.ReadSmallFileBytesAsync(ct);
            byte[] patchedData = await UniversalPatcher.ApplyPatchAsync(originalData, patchData, null, ct);

            log?.Invoke($"패치완료: {patchKey}", LogLevel.Info);

            AppliedCount++;
            _resultCache[fullPath] = patchedData;

            return new MemoryStream(patchedData);
        }

        return null;
    }

    private Stream? OpenDirect(string relative)
    {
        if (_diskFolder != null)
        {
            string localPath = Path.Combine(_diskFolder, relative.Replace('/', Path.DirectorySeparatorChar));

            return File.Exists(localPath) ? File.OpenRead(localPath) : null;
        }

        string zipKey = _zipPrefix + relative;
        var entry = _zipArchive!.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), zipKey, StringComparison.OrdinalIgnoreCase));

        return entry?.Open();
    }

    private Dictionary<string, PatchFileRef> GetOrBuildPatchIndex()
    {
        if (_patchIndex != null)
            return _patchIndex;

        var index = new Dictionary<string, PatchFileRef>(StringComparer.OrdinalIgnoreCase);

        if (_diskFolder != null)
        {
            if (Directory.Exists(_diskFolder))
            {
                foreach (string path in Directory.EnumerateFiles(_diskFolder, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(path);

                    if (PatchExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                        index.TryAdd(name, PatchFileRef.FromDisk(path));
                }
            }
        }
        else
        {
            foreach (var entry in _zipArchive!.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                string key = entry.FullName.Replace('\\', '/');

                if (!key.StartsWith(_zipPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (PatchExtensions.Any(ext => entry.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    var capturedEntry = entry;

                    index.TryAdd(entry.Name, PatchFileRef.FromZip(() => capturedEntry.Open(), capturedEntry.Length));
                }
            }
        }

        _patchIndex = index;

        return _patchIndex;
    }
}