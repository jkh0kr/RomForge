using Common;
using Patch.Core;
using Patch.Core.Services;
using System.IO.Compression;
using WiiU.Core.Models;

namespace WiiU.Core.Services;

public sealed class WiiURepackService
{
    private const int BufferSize = 1024 * 1024;
    private static readonly string[] PatchAnchors = ["content", "meta", "code"];

    public static void Repack(ITitleSource source, string outputWuaPath, string? patchFolder = null, string? titleIdHexOverride = null, int? titleVersionOverride = null, Action<int, int, string>? onFileProgress = null, Action<string, LogLevel>? log = null, CancellationToken ct = default)
    {
        RepackMultiple([new RepackEntry(source, patchFolder, titleIdHexOverride, titleVersionOverride)], outputWuaPath, onFileProgress, log, ct);
    }

    public static void RepackMultiple(IReadOnlyList<RepackEntry> entries, string outputWuaPath, Action<int, int, string>? onFileProgress = null, Action<string, LogLevel>? log = null, CancellationToken ct = default)
    {
        if (entries.Count == 0)
            throw new ArgumentException("At least one entry is required.", nameof(entries));

        var resolved = new List<(string TitleFolder, ITitleSource Source, Dictionary<string, PatchFileRef> OverwriteFiles, Dictionary<string, PatchFileRef> BinaryPatches, List<string> Paths)>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openZips = new List<ZipArchive>(); // 쓰기 루프가 끝날 때까지 살아있어야 zip 엔트리 스트림을 열 수 있음

        try
        {
            foreach (var entry in entries)
            {
                string titleIdHex = entry.TitleIdHexOverride ?? entry.Source.TitleIdHex;
                int titleVersion = entry.TitleVersionOverride ?? entry.Source.TitleVersion;
                string titleFolder = $"{titleIdHex}_v{titleVersion}";

                if (!seenFolders.Add(titleFolder))
                    throw new InvalidOperationException($"Two entries both resolved to the folder name \"{titleFolder}\" — they'd collide in the output .wua. " + "Check that base/update/DLC don't share the same title ID + version.");

                var overwriteFiles = new Dictionary<string, PatchFileRef>(StringComparer.Ordinal);
                var binaryPatches = new Dictionary<string, PatchFileRef>(StringComparer.Ordinal);

                if (entry.PatchFolder is not null)
                {
                    PatchFileIndex index;

                    if (ZipPatchSource.IsZipPath(entry.PatchFolder))
                    {
                        var archive = ZipFile.OpenRead(entry.PatchFolder);

                        openZips.Add(archive);

                        string prefix = ZipPatchSource.FindPatchRoot(archive, PatchAnchors) ?? "";

                        index = PatchFileIndex.Build(archive, prefix);
                    }
                    else
                    {
                        string effectivePatchFolder = PatchFolderResolver.FindPatchRoot(entry.PatchFolder, PatchAnchors) ?? entry.PatchFolder;

                        index = PatchFileIndex.Build(effectivePatchFolder);
                    }

                    foreach (var patchEntry in index.Entries)
                    {
                        string relDir = patchEntry.RelativeDir.Replace(Path.DirectorySeparatorChar, '/');
                        string relTarget = relDir.Length == 0 ? patchEntry.BaseName : $"{relDir}/{patchEntry.BaseName}";

                        if (patchEntry.Kind == PatchFileKind.BinaryPatch)
                            binaryPatches[relTarget] = patchEntry.File;
                        else
                            overwriteFiles[relTarget] = patchEntry.File;
                    }
                }

                var sourcePaths = new HashSet<string>(entry.Source.EnumerateFiles(), StringComparer.Ordinal);
                int matchedCount = overwriteFiles.Keys.Count(sourcePaths.Contains) + binaryPatches.Keys.Count(sourcePaths.Contains);

                if (entry.PatchFolder is not null && matchedCount == 0)
                    log?.Invoke($"패치 대상 파일이 존재하지 않습니다. ({titleFolder})", LogLevel.Error);

                var paths = new SortedSet<string>(sourcePaths, StringComparer.Ordinal);

                foreach (var p in overwriteFiles.Keys)
                    paths.Add(p);

                resolved.Add((titleFolder, entry.Source, overwriteFiles, binaryPatches, new List<string>(paths)));
            }

            int total = 0;

            foreach (var (TitleFolder, Source, OverwriteFiles, BinaryPatches, Paths) in resolved)
                total += Paths.Count;

            int done = 0;
            using var outStream = File.Create(outputWuaPath);
            using var writer = new WuaWriter(outStream);
            var buffer = new byte[BufferSize];

            foreach (var (titleFolder, source, overwriteFiles, binaryPatches, paths) in resolved)
            {
                writer.MakeDir(titleFolder, recursive: true);

                var writtenDirs = new HashSet<string>(StringComparer.Ordinal);

                foreach (string path in paths)
                {
                    ct.ThrowIfCancellationRequested();

                    EnsureDirWritten(writer, titleFolder, GetDirectoryPart(path), writtenDirs);
                    writer.StartNewFile($"{titleFolder}/{path}");

                    using (Stream srcStream = ResolveEntryStream(path, source, overwriteFiles, binaryPatches, log, ct))
                    {
                        int read;

                        while ((read = srcStream.Read(buffer, 0, buffer.Length)) > 0)
                            writer.AppendData(buffer.AsSpan(0, read));
                    }

                    done++;

                    onFileProgress?.Invoke(done, total, $"{titleFolder}/{path}");
                }
            }

            writer.FinalizeArchive();
        }
        finally
        {
            foreach (var archive in openZips)
                archive.Dispose();
        }
    }

    private static Stream ResolveEntryStream(string path, ITitleSource source, Dictionary<string, PatchFileRef> overwriteFiles, Dictionary<string, PatchFileRef> binaryPatches, Action<string, LogLevel>? log, CancellationToken ct)
    {
        if (overwriteFiles.TryGetValue(path, out var overwriteRef))
            return overwriteRef.OpenRead();

        if (binaryPatches.TryGetValue(path, out var patchRef))
        {
            byte[] originalData;

            using (var srcStream = source.OpenRead(path))
            using (var ms = new MemoryStream())
            {
                srcStream.CopyTo(ms);
                originalData = ms.ToArray();
            }

            byte[] patchData = patchRef.ReadSmallFileBytes();
            byte[] patchedData = UniversalPatcher.ApplyPatchAsync(originalData, patchData, null, ct).GetAwaiter().GetResult();

            log?.Invoke($"  패치 완료: {patchRef.DisplayName} → {path}", LogLevel.Info);

            return new MemoryStream(patchedData);
        }

        return source.OpenRead(path);
    }

    private static string GetDirectoryPart(string path)
    {
        int idx = path.LastIndexOf('/');

        return idx < 0 ? "" : path[..idx];
    }

    private static void EnsureDirWritten(WuaWriter writer, string titleFolderName, string dirPath, HashSet<string> writtenDirs)
    {
        if (dirPath.Length == 0 || !writtenDirs.Add(dirPath)) 
            return;

        EnsureDirWritten(writer, titleFolderName, GetDirectoryPart(dirPath), writtenDirs);
        writer.MakeDir($"{titleFolderName}/{dirPath}", recursive: true);
    }
}