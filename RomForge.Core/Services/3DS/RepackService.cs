using _3DS.Core.Crypto;
using _3DS.Core.FileSystem;
using _3DS.Core.Interfaces;
using _3DS.Core.Models;
using _3DS.Core.Services;
using Common;
using NSW.Utils;
using Patch.Core.Services;
using RomForge.Core.Models._3DS;
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

    public async Task<string> RepackAsync(string unpackedPath, string outputPath, string? displayName, string? gameName, string? publisher = null, KeyStore? keyStore = null, RepackOutputFormat format = RepackOutputFormat.Cci, Action<long, long>? reporter = null, Action<string>? onOutputPathKnown = null, CancellationToken ct = default)
    {
        log("리팩 시작...", LogLevel.Highlight);

        var repackedNcchs = new Dictionary<int, (NcchUnpackResult, byte[], Stream, RomFsUnpackResult?, IRomFsFileSource?)>();
        var contentsList = new List<Contents>();
        int exefsPatchedCount = 0;
        PatchFolderFileSource? romfsPatchSource = null;
        byte[]? exHeaderPart0 = null;
        byte[]? exefsBlockPart0 = null;
        string? titleId = null;

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

            if (idx == 0)
            {
                exHeaderPart0 = exHeader;
                exefsBlockPart0 = exefsBlock;
                titleId = ncchHeader.ProgramId.ToString("x16");
            }

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

        string safeFileName = NspNameBuilder.SafeFileName(displayName);
        string fileName = string.IsNullOrEmpty(safeFileName) ? "output" : safeFileName;
        string namePart = string.IsNullOrEmpty(titleId) ? fileName : $"{fileName} [{titleId.ToUpperInvariant()}]";
        string outputCci = Utils.GetUniqueFilePath(Path.Combine(outputPath, namePart + "_Repack.cci"));

        var repackedSource = await RepackedNcsdSource.CreateAsync(repackedNcchs, contentsList, log, ct);

        string outputFilePath = await BuildOutputAsync(repackedSource, outputCci, keyStore, format, exHeaderPart0, exefsBlockPart0, reporter, onOutputPathKnown, ct);

        if (patchDirSpecified && exefsPatchedCount == 0 && (romfsPatchSource == null || romfsPatchSource.AppliedCount == 0))
            log("패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
        else
        {
            if (exefsPatchedCount > 0)
                log($"exefs 패치 적용 완료: {exefsPatchedCount}개 파일", LogLevel.Ok);

            if (romfsPatchSource is { AppliedCount: > 0 })
                log($"romfs 패치 적용 완료: {romfsPatchSource.AppliedCount}개 파일", LogLevel.Ok);
        }

        log($"출력: {outputFilePath}", LogLevel.Ok);

        return outputFilePath;
    }

    public async Task<string> RepackDirectAsync(string inputPath, string outputCci, KeyStore keyStore, string? gameName = null, string? publisher = null, RepackOutputFormat format = RepackOutputFormat.Cci, Action<long, long>? reporter = null, Action<string>? onOutputPathKnown = null, CancellationToken ct = default)
    {
        log("스트리밍 기반 리팩 시작...", LogLevel.Highlight);

        await using var source = await OpenSourceAsync(inputPath, keyStore, ct);

        var repackedNcchs = new Dictionary<int, (NcchUnpackResult, byte[], Stream, RomFsUnpackResult?, IRomFsFileSource?)>();
        int exefsPatchedCount = 0;
        PatchFolderFileSource? romfsPatchSource = null;
        byte[]? exHeaderPart0 = null;
        byte[]? exefsBlockPart0 = null;

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

            if (idx == 0)
            {
                exHeaderPart0 = unpack.ExHeader;
                exefsBlockPart0 = exefsBlock;
            }

            IRomFsFileSource? patchSource = null;

            if (idx == 0)
            {
                romfsPatchSource = patchCtx.CreateRomfsSource("romfs");
                patchSource = romfsPatchSource;
            }

            repackedNcchs[idx] = (unpack, exefsBlock, ncchStream, unpack.RomFs, patchSource);
        }

        var repackedSource = await RepackedNcsdSource.CreateAsync(repackedNcchs, source.Contents, log, ct);

        string outputFilePath = await BuildOutputAsync(repackedSource, outputCci, keyStore, format, exHeaderPart0, exefsBlockPart0, reporter, onOutputPathKnown, ct);

        if (patchDirSpecified && exefsPatchedCount == 0 && (romfsPatchSource == null || romfsPatchSource.AppliedCount == 0))
            log("패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
        else
        {
            if (exefsPatchedCount > 0)
                log($"exefs 패치 적용 완료: {exefsPatchedCount}개 파일", LogLevel.Ok);

            if (romfsPatchSource is { AppliedCount: > 0 })
                log($"romfs 패치 적용 완료: {romfsPatchSource.AppliedCount}개 파일", LogLevel.Ok);
        }

        log($"출력: {outputFilePath}", LogLevel.Ok);

        return outputFilePath;
    }

    private async Task<string> BuildOutputAsync(RepackedNcsdSource repackedSource, string outputCci, KeyStore? keyStore, RepackOutputFormat format, byte[]? exHeaderPart0, byte[]? exefsBlockPart0, Action<long, long>? reporter, Action<string>? onOutputPathKnown, CancellationToken ct)
    {
        if (format == RepackOutputFormat.Cia)
        {
            if (keyStore == null)
                throw new InvalidOperationException("CIA를 생성하려면 키가 필요합니다.");

            string outputCia = Utils.GetUniqueFilePath(Path.ChangeExtension(outputCci, ".cia"));

            await using var ciaStream = File.Open(outputCia, FileMode.Create, FileAccess.ReadWrite);
            onOutputPathKnown?.Invoke(outputCia);

            byte[]? smdhPart0 = ExtractIcon(exefsBlockPart0);

            await CiaBuilder.BuildAsync(repackedSource, keyStore, ciaStream, exHeaderPart0, smdhPart0, reporter, log, ct);

            return outputCia;
        }

        await using var cciStream = File.Open(outputCci, FileMode.Create, FileAccess.ReadWrite);
        onOutputPathKnown?.Invoke(outputCci);

        await NcsdBuilder.BuildAsync(repackedSource, cciStream, reporter, ct);

        return outputCci;
    }

    private static byte[]? ExtractIcon(byte[]? exefsBlock)
    {
        if (exefsBlock == null)
            return null;

        const uint smdhMagic = 0x48444D53;
        const int iconSize = 0x36C0;

        for (int i = 0; i <= exefsBlock.Length - 4; i++)
        {
            if (BitConverter.ToUInt32(exefsBlock, i) != smdhMagic)
                continue;

            if (i + iconSize > exefsBlock.Length)
                return null;

            byte[] iconData = new byte[iconSize];
            Array.Copy(exefsBlock, i, iconData, 0, iconSize);

            return iconData;
        }

        return null;
    }

    private static void ApplySmdhToMemory(byte[] exefsBlock, string? gameName, string? publisher, Action<string, LogLevel> log)
    {
        const uint smdhMagic = 0x48444D53;
        const int headerSize = 0x200;
        const int maxEntries = 8;
        const int iconSize = 0x36C0;

        for (int i = 0; i <= exefsBlock.Length - 4; i++)
        {
            if (BitConverter.ToUInt32(exefsBlock, i) != smdhMagic)
                continue;

            byte[] iconData = new byte[iconSize];
            Array.Copy(exefsBlock, i, iconData, 0, iconSize);

            byte[]? overridden = SmdhWriter.ApplyOverride(iconData, gameName, publisher);

            if (overridden == null)
                continue;

            Array.Copy(overridden, 0, exefsBlock, i, iconSize);

            uint dataOffset = (uint)(i - headerSize);
            int entryIndex = -1;

            for (int e = 0; e < maxEntries; e++)
            {
                int entryBase = e * 0x10;
                uint entryOffset = BitConverter.ToUInt32(exefsBlock, entryBase + 8);
                uint entrySize = BitConverter.ToUInt32(exefsBlock, entryBase + 12);

                if (entryOffset == dataOffset && entrySize == iconSize)
                {
                    entryIndex = e;
                    break;
                }
            }

            if (entryIndex < 0)
            {
                log("⚠️ exefs 헤더에서 icon 엔트리를 찾지 못해 해시를 갱신하지 못했습니다.", LogLevel.Error);
                return;
            }

            int hashBase = headerSize - 0x100 + (maxEntries - 1 - entryIndex) * 0x20;
            byte[] newHash = System.Security.Cryptography.SHA256.HashData(overridden);
            Array.Copy(newHash, 0, exefsBlock, hashBase, newHash.Length);

            log("게임명/배급사 정보를 변경했습니다.", LogLevel.Ok);
            return;
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
            ".cci" or ".zcci" or ".3ds" => await CciSource.OpenAsync(inputPath, keyStore, (msg, level) => log(msg, level), ct),
            _ => throw new NotSupportedException($"지원하지 않는 파일 형식: {ext}")
        };
    }
}