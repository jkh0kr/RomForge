using _3DS.Core.Crypto;
using _3DS.Core.FileSystem;
using _3DS.Core.Interfaces;
using _3DS.Core.Models;
using _3DS.Core.Services;
using Common;
using NSW.Utils;
using Patch.Core.Services;
using System.IO;

namespace RomForge.Core.Services._3DS;

public class RepackService(Action<string, LogLevel> log, Func<string?> getPatchPath)
{
    public async Task UnpackAsync(string inputPath, string unpackedPath, KeyStore keyStore, Action<long, long>? reporter = null, CancellationToken ct = default)
    {
        log("언팩 시작...", LogLevel.Highlight);

        await using var source = await OpenSourceAsync(inputPath, keyStore, ct);

        long totalBytes = 0;

        foreach (var content in source.Contents)
        {
            var (ncchStream, _) = await source.OpenContentDecrypted(content.ContentIndex);

            await using (ncchStream)
            {
                byte[] hdrBuf = new byte[NcchHeader.Size];

                await ncchStream.ReadExactlyAsync(hdrBuf, ct);

                var ncchHeader = NcchHeader.Parse(hdrBuf);

                totalBytes += ((long)ncchHeader.ExefsSize * 0x200) + ((long)ncchHeader.RomfsSize * 0x200);
            }
        }

        long accumulatedBytes = 0;

        foreach (var content in source.Contents)
        {
            int idx = content.ContentIndex;
            var (ncchStream, _) = await source.OpenContentDecrypted(idx);

            await using (ncchStream)
            {
                byte[] hdrBuf = new byte[NcchHeader.Size];

                await ncchStream.ReadExactlyAsync(hdrBuf, ct);

                var ncchHeader = NcchHeader.Parse(hdrBuf);

                ncchStream.Position = 0;

                var unpack = await NcchUnpacker.UnpackAsync(ncchStream, ncchHeader, ct);
                string partDir = Path.Combine(unpackedPath, $"partition{idx}");
                long lastPartitionCurrent = 0;

                Action<long, long>? partitionReporter = null;

                if (reporter != null && totalBytes > 0)
                {
                    partitionReporter = (current, total) =>
                    {
                        long delta = current - lastPartitionCurrent;

                        if (delta > 0)
                        {
                            accumulatedBytes += delta;
                            lastPartitionCurrent = current;
                            reporter(accumulatedBytes, totalBytes);
                        }
                    };
                }

                await NcchUnpacker.SaveToDirectoryAsync(ncchStream, unpack, partDir, content, partitionReporter, ct);

                log($"파티션 {idx} 언팩 완료", LogLevel.Info);
            }
        }

        if (reporter != null && totalBytes > 0)
            reporter(totalBytes, totalBytes);
    }

    public async Task<string> RepackAsync(string unpackedPath, string outputPath, string? gameName, string? publisher = null, Action<long, long>? reporter = null, CancellationToken ct = default)
    {
        log("리팩 시작...", LogLevel.Highlight);

        string safeFileName = NspNameBuilder.SafeFileName(gameName);
        string fileName = string.IsNullOrEmpty(safeFileName) ? "output" : safeFileName;
        string outputCci = Utils.GetUniqueFilePath(Path.Combine(outputPath, fileName + "_Repack.cci"));
        var repackedNcchs = new Dictionary<int, (NcchUnpackResult, byte[], Stream, RomFsUnpackResult?, IRomFsFileSource?)>();
        var contentsList = new List<Contents>();
        int exefsPatchedCount = 0;
        PatchFolderFileSource? romfsPatchSource = null;

        using var patchCtx = PatchSourceContext.Open(getPatchPath(), log);
        bool patchDirSpecified = patchCtx.HasSource;

        var partitionIndices = Directory.GetDirectories(unpackedPath, "partition*")
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name!.AsSpan("partition".Length), out int i) ? i : (int?)null)
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .OrderBy(i => i)
            .ToList();

        foreach (int idx in partitionIndices)
        {
            string partDir = Path.Combine(unpackedPath, $"partition{idx}");
            string headerPath = Path.Combine(partDir, "header.bin");

            if (!File.Exists(headerPath))
                throw new FileNotFoundException($"header.bin 없음: {partDir}");

            byte[] headerRaw = await File.ReadAllBytesAsync(headerPath, ct);
            var ncchHeader = NcchHeader.Parse(headerRaw);

            string contentPath = Path.Combine(partDir, "content.bin");

            if (!File.Exists(contentPath))
                throw new FileNotFoundException($"content.bin 없음: {partDir}");

            byte[] contentRaw = await File.ReadAllBytesAsync(contentPath, ct);
            using var cms = new MemoryStream(contentRaw);
            using var cbr = new BinaryReader(cms);
            var contents = new Contents
            {
                ContentId = cbr.ReadUInt32(),
                ContentIndex = cbr.ReadUInt16(),
                ContentType = cbr.ReadUInt16(),
            };

            contentsList.Add(contents);

            byte[]? exHeader = null;
            byte[]? logo = null;
            byte[]? plainRegion = null;
            string exHeaderPath = Path.Combine(partDir, "exheader.bin");
            string logoPath = Path.Combine(partDir, "logo.bin");
            string plainPath = Path.Combine(partDir, "plain.bin");

            if (File.Exists(exHeaderPath))
                exHeader = await File.ReadAllBytesAsync(exHeaderPath, ct);

            if (File.Exists(logoPath))
                logo = await File.ReadAllBytesAsync(logoPath, ct);

            if (File.Exists(plainPath))
                plainRegion = await File.ReadAllBytesAsync(plainPath, ct);

            string exefsDir = Path.Combine(partDir, "exefs");
            var exefsFiles = Directory.Exists(exefsDir) ? ExeFsUnpacker.LoadFromDirectory(exefsDir) : [];
            byte[] exefsBlock = [];

            if (exefsFiles.Count > 0)
            {
                var exefsIndex = idx == 0 ? patchCtx.FindSubIndex("exefs") : null;
                var rootIndex = idx == 0 ? patchCtx.RootIndex() : null;
                var (data, patchedCount) = await ExeFsPacker.PackWithPatchAsync(exefsFiles, exefsIndex, exHeader, rootIndex, log, ct);

                exefsBlock = data;
                exefsPatchedCount += patchedCount;
            }

            if (idx == 0 && exefsBlock.Length > 0 && (!string.IsNullOrEmpty(gameName) || !string.IsNullOrEmpty(publisher)))
                ApplySmdhToMemory(exefsBlock, gameName, publisher, log);

            string romfsDir = Path.Combine(partDir, "romfs");
            RomFsUnpackResult? romfsResult = null;
            IRomFsFileSource? romfsSource = null;

            if (Directory.Exists(romfsDir))
            {
                romfsResult = RomFsPacker.ScanFolderAsUnpackResult(romfsDir);

                IRomFsFileSource? patchSource = null;

                if (idx == 0)
                {
                    romfsPatchSource = patchCtx.CreateRomfsSource("romfs");
                    patchSource = romfsPatchSource;
                }

                romfsSource = new FolderRomFsFileSource(romfsDir, patchSource);
            }

            var unpackResult = new NcchUnpackResult
            {
                Header = ncchHeader,
                ExHeader = exHeader,
                Logo = logo,
                PlainRegion = plainRegion,
                ExeFs = null,
                RomFs = romfsResult,
            };

            repackedNcchs[idx] = (unpackResult, exefsBlock, Stream.Null, romfsResult, romfsSource);
        }

        if (repackedNcchs.Count == 0)
            throw new InvalidOperationException("언팩된 파티션이 없습니다.");

        var repackedSource = await RepackedNcsdSource.CreateAsync(repackedNcchs, contentsList, log, ct);

        await using var outputStream = File.Open(outputCci, FileMode.Create, FileAccess.ReadWrite);

        await NcsdBuilder.BuildAsync(repackedSource, outputStream, reporter, ct);

        if (patchDirSpecified && exefsPatchedCount == 0 && (romfsPatchSource == null || romfsPatchSource.AppliedCount == 0))
            log("패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
        else
        {
            if (exefsPatchedCount > 0)
                log($"exefs 패치 적용 완료: {exefsPatchedCount}개 파일", LogLevel.Ok);

            if (romfsPatchSource is { AppliedCount: > 0 })
                log($"romfs 패치 적용 완료: {romfsPatchSource.AppliedCount}개 파일", LogLevel.Ok);
        }

        log($"출력: {outputCci}", LogLevel.Ok);

        return outputCci;
    }

    public async Task RepackDirectAsync(string inputPath, string outputCci, KeyStore keyStore, string? gameName = null, string? publisher = null, Action<long, long>? reporter = null, CancellationToken ct = default)
    {
        log("스트리밍 기반 리팩 시작...", LogLevel.Highlight);

        await using var source = await OpenSourceAsync(inputPath, keyStore, ct);

        var repackedNcchs = new Dictionary<int, (NcchUnpackResult, byte[], Stream, RomFsUnpackResult?, IRomFsFileSource?)>();
        int exefsPatchedCount = 0;
        PatchFolderFileSource? romfsPatchSource = null;

        using var patchCtx = PatchSourceContext.Open(getPatchPath(), log);
        bool patchDirSpecified = patchCtx.HasSource;

        foreach (var content in source.Contents)
        {
            int idx = content.ContentIndex;
            var (ncchStream, _) = await source.OpenContentDecrypted(idx);
            byte[] hdrBuf = new byte[NcchHeader.Size];

            await ncchStream.ReadExactlyAsync(hdrBuf, ct);

            var ncchHeader = NcchHeader.Parse(hdrBuf);

            ncchStream.Position = 0;

            var unpack = await NcchUnpacker.UnpackAsync(ncchStream, ncchHeader, ct);

            byte[] exefsBlock = [];

            if (unpack.ExeFs != null)
            {
                IReadOnlyList<ExeFsFile> exefsSourceFiles = unpack.ExeFs.Files;

                var exefsIndex = idx == 0 ? patchCtx.FindSubIndex("exefs") : null;
                var rootIndex = idx == 0 ? patchCtx.RootIndex() : null;
                var (data, patchedCount) = await ExeFsPacker.PackWithPatchAsync(exefsSourceFiles, exefsIndex, unpack.ExHeader, rootIndex, log, ct);

                exefsBlock = data;
                exefsPatchedCount += patchedCount;
            }

            if (idx == 0 && exefsBlock.Length > 0 && (!string.IsNullOrEmpty(gameName) || !string.IsNullOrEmpty(publisher)))
                ApplySmdhToMemory(exefsBlock, gameName, publisher, log);

            IRomFsFileSource? patchSource = null;

            if (idx == 0)
            {
                romfsPatchSource = patchCtx.CreateRomfsSource("romfs");
                patchSource = romfsPatchSource;
            }

            repackedNcchs[idx] = (unpack, exefsBlock, ncchStream, unpack.RomFs, patchSource);
        }

        var repackedSource = await RepackedNcsdSource.CreateAsync(repackedNcchs, source.Contents, log, ct);

        await using var outputStream = File.Open(outputCci, FileMode.Create, FileAccess.ReadWrite);

        await NcsdBuilder.BuildAsync(repackedSource, outputStream, reporter, ct);

        if (patchDirSpecified && exefsPatchedCount == 0 && (romfsPatchSource == null || romfsPatchSource.AppliedCount == 0))
            log("패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
        else
        {
            if (exefsPatchedCount > 0)
                log($"exefs 패치 적용 완료: {exefsPatchedCount}개 파일", LogLevel.Ok);

            if (romfsPatchSource is { AppliedCount: > 0 })
                log($"romfs 패치 적용 완료: {romfsPatchSource.AppliedCount}개 파일", LogLevel.Ok);
        }

        log($"출력: {outputCci}", LogLevel.Ok);
    }

    private static void ApplySmdhToMemory(byte[] exefsBlock, string? gameName, string? publisher, Action<string, LogLevel> log)
    {
        const uint smdhMagic = 0x48444D53; // "SMDH"
        for (int i = 0; i <= exefsBlock.Length - 4; i++)
        {
            if (BitConverter.ToUInt32(exefsBlock, i) == smdhMagic)
            {
                byte[] iconData = new byte[0x36C0];
                Array.Copy(exefsBlock, i, iconData, 0, 0x36C0);

                byte[]? overridden = SmdhWriter.ApplyOverride(iconData, gameName, publisher);
                if (overridden != null)
                {
                    Array.Copy(overridden, 0, exefsBlock, i, 0x36C0);
                    log("게임명/배급사 정보를 변경했습니다.", LogLevel.Ok);
                    return;
                }
            }
        }
    }

    private sealed class PatchSourceContext : IDisposable
    {
        private readonly string? _diskPath;
        private readonly IArchivePatchSource? _archive;

        public bool HasSource { get; }

        private PatchSourceContext(string? diskPath, IArchivePatchSource? archive, bool hasSource)
        {
            _diskPath = diskPath;
            _archive = archive;
            HasSource = hasSource;
        }

        public static PatchSourceContext Open(string? rawPath, Action<string, LogLevel> log)
        {
            if (string.IsNullOrEmpty(rawPath))
                return new PatchSourceContext(null, null, false);

            if (ArchivePatchSourceFactory.IsArchivePath(rawPath))
            {
                try
                {
                    return new PatchSourceContext(null, ArchivePatchSourceFactory.Open(rawPath), true);
                }
                catch (Exception ex)
                {
                    log($"⚠️ 한글패치 압축파일을 열 수 없습니다: {ex.Message}", LogLevel.Error);

                    return new PatchSourceContext(null, null, false);
                }
            }

            if (!Directory.Exists(rawPath))
            {
                log($"⚠️ 한글패치 경로를 찾을 수 없습니다: {rawPath}", LogLevel.Error);

                return new PatchSourceContext(null, null, false);
            }

            return new PatchSourceContext(rawPath, null, true);
        }

        public PatchFileIndex? FindSubIndex(string folderName)
        {
            if (!HasSource)
                return null;

            if (_archive != null)
            {
                string? prefix = ArchivePatchFolderResolver.FindSubDir(_archive.EntryPaths, folderName);

                return prefix == null ? null : PatchFileIndex.Build(_archive, prefix);
            }

            string? dir = PatchFolderResolver.FindSubDir(_diskPath!, folderName);

            return dir == null ? null : PatchFileIndex.Build(dir);
        }

        public PatchFileIndex? RootIndex()
        {
            if (!HasSource)
                return null;

            var combined = new PatchFileIndex();

            if (_archive != null)
            {
                combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(_archive, "").Entries);

                string? exefsPrefix = ArchivePatchFolderResolver.FindSubDir(_archive.EntryPaths, "exefs");

                if (exefsPrefix != null)
                    combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(_archive, ParentPrefix(exefsPrefix)).Entries);

                string? romfsPrefix = ArchivePatchFolderResolver.FindSubDir(_archive.EntryPaths, "romfs");

                if (romfsPrefix != null)
                    combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(_archive, ParentPrefix(romfsPrefix)).Entries);

                foreach (string codeFileName in new[] { "code.bin", "code.ips" })
                {
                    string? codeParent = FindFileParentPrefix(_archive.EntryPaths, codeFileName);

                    if (codeParent != null)
                        combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(_archive, codeParent).Entries);
                }
            }
            else
            {
                combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(_diskPath!).Entries);

                string? exefsDir = PatchFolderResolver.FindSubDir(_diskPath!, "exefs");

                if (exefsDir != null)
                    combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(Path.GetDirectoryName(exefsDir)!).Entries);

                string? romfsDir = PatchFolderResolver.FindSubDir(_diskPath!, "romfs");

                if (romfsDir != null)
                    combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(Path.GetDirectoryName(romfsDir)!).Entries);

                foreach (string codeFileName in new[] { "code.bin", "code.ips" })
                {
                    string? codeParent = FindFileParentDir(_diskPath!, codeFileName);

                    if (codeParent != null)
                        combined.Entries.AddRange(PatchFileIndex.BuildTopLevelOnly(codeParent).Entries);
                }
            }

            return combined.HasAnyFile ? combined : null;
        }

        private static string ParentPrefix(string subDirPrefix)
        {
            string trimmed = subDirPrefix.TrimEnd('/');
            int lastSlash = trimmed.LastIndexOf('/');

            return lastSlash < 0 ? "" : trimmed[..(lastSlash + 1)];
        }

        private static string? FindFileParentPrefix(IReadOnlyList<string> entryPaths, string fileName)
        {
            string? best = null;
            int bestDepth = int.MaxValue;

            foreach (string path in entryPaths)
            {
                int lastSlash = path.LastIndexOf('/');
                string name = lastSlash < 0 ? path : path[(lastSlash + 1)..];

                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string parent = lastSlash < 0 ? "" : path[..(lastSlash + 1)];
                int depth = parent.Count(c => c == '/');

                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    best = parent;
                }
            }

            return best;
        }

        private static string? FindFileParentDir(string root, string fileName)
        {
            if (!Directory.Exists(root))
                return null;

            string? best = null;
            int bestDepth = int.MaxValue;

            foreach (string file in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
            {
                string? parent = Path.GetDirectoryName(file);

                if (parent == null)
                    continue;

                string relative = Path.GetRelativePath(root, parent);
                int depth = relative == "." ? 0 : relative.Count(c => c == Path.DirectorySeparatorChar) + 1;

                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    best = parent;
                }
            }

            return best;
        }

        public PatchFolderFileSource? CreateRomfsSource(string folderName)
        {
            if (!HasSource)
                return null;

            if (_archive != null)
            {
                string? prefix = ArchivePatchFolderResolver.FindSubDir(_archive.EntryPaths, folderName);

                return prefix == null ? null : PatchFolderFileSource.ForArchive(_archive, prefix);
            }

            string? dir = PatchFolderResolver.FindSubDir(_diskPath!, folderName);

            return dir == null ? null : PatchFolderFileSource.ForFolder(dir);
        }

        public void Dispose() => _archive?.Dispose();
    }

    private async Task<INcsdSource> OpenSourceAsync(string inputPath, KeyStore keyStore, CancellationToken ct)
    {
        string ext = Path.GetExtension(inputPath).ToLowerInvariant();

        return ext switch
        {
            ".cia" => await new CiaReader(keyStore).OpenAsync(inputPath, (msg, level) => log(msg, level), ct),
            ".cci" or ".zcci" or ".3ds"=> await CciSource.OpenAsync(inputPath, keyStore, (msg, level) => log(msg, level), ct),
            _ => throw new NotSupportedException($"지원하지 않는 파일 형식: {ext}")
        };
    }
}