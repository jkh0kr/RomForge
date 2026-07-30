using _3DS.Core.Crypto;
using _3DS.Core.Interfaces;
using _3DS.Core.Models;
using _3DS.Core.Services;
using Common;
using NSW.Utils;
using Patch.Core.Services;
using System.IO;
using System.IO.Compression;

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

    public async Task RepackAsync(string unpackedPath, string outputPath, string? gameName, Action<long, long>? reporter = null, CancellationToken ct = default)
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

        log($"출력: {outputCci}", LogLevel.Ok);
    }

    public async Task RepackDirectAsync(string inputPath, string outputCci, KeyStore keyStore, Action<long, long>? reporter = null, CancellationToken ct = default)
    {
        log("메모리 기반 리팩 시작...", LogLevel.Highlight);

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
                var exefsIndex = idx == 0 ? patchCtx.FindSubIndex("exefs") : null;
                var rootIndex = idx == 0 ? patchCtx.RootIndex() : null;
                var (data, patchedCount) = await ExeFsPacker.PackWithPatchAsync(unpack.ExeFs.Files, exefsIndex, unpack.ExHeader, rootIndex, log, ct);

                exefsBlock = data;
                exefsPatchedCount += patchedCount;
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

        await using var outputStream = File.Open(outputCci, FileMode.Create, FileAccess.ReadWrite);

        await NcsdBuilder.BuildAsync(repackedSource, outputStream, reporter, ct);

        if (patchDirSpecified && exefsPatchedCount == 0 && (romfsPatchSource == null || romfsPatchSource.AppliedCount == 0))
            log("패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);

        log($"출력: {outputCci}", LogLevel.Ok);
    }

    private sealed class PatchSourceContext : IDisposable
    {
        private readonly string? _diskPath;
        private readonly ZipArchive? _zip;

        public bool HasSource { get; }

        private PatchSourceContext(string? diskPath, ZipArchive? zip, bool hasSource)
        {
            _diskPath = diskPath;
            _zip = zip;
            HasSource = hasSource;
        }

        public static PatchSourceContext Open(string? rawPath, Action<string, LogLevel> log)
        {
            if (string.IsNullOrEmpty(rawPath))
                return new PatchSourceContext(null, null, false);

            if (ZipPatchSource.IsZipPath(rawPath))
            {
                try
                {
                    return new PatchSourceContext(null, ZipFile.OpenRead(rawPath), true);
                }
                catch (Exception ex)
                {
                    log($"⚠️ 한글패치 zip을 열 수 없습니다: {ex.Message}", LogLevel.Error);

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

            if (_zip != null)
            {
                string? prefix = ZipPatchSource.FindSubDir(_zip, folderName);

                return prefix == null ? null : PatchFileIndex.Build(_zip, prefix);
            }

            string? dir = PatchFolderResolver.FindSubDir(_diskPath!, folderName);

            return dir == null ? null : PatchFileIndex.Build(dir);
        }

        public PatchFileIndex? RootIndex()
        {
            if (!HasSource)
                return null;

            return _zip != null ? PatchFileIndex.Build(_zip, "") : PatchFileIndex.Build(_diskPath!);
        }

        public PatchFolderFileSource? CreateRomfsSource(string folderName)
        {
            if (!HasSource)
                return null;

            if (_zip != null)
            {
                string? prefix = ZipPatchSource.FindSubDir(_zip, folderName);

                return prefix == null ? null : PatchFolderFileSource.ForZip(_zip, prefix);
            }

            string? dir = PatchFolderResolver.FindSubDir(_diskPath!, folderName);

            return dir == null ? null : PatchFolderFileSource.ForFolder(dir);
        }

        public void Dispose() => _zip?.Dispose();
    }

    private async Task<INcsdSource> OpenSourceAsync(string inputPath, KeyStore keyStore, CancellationToken ct)
    {
        string ext = Path.GetExtension(inputPath).ToLowerInvariant();

        return ext switch
        {
            ".cia" => await new CiaReader(keyStore).OpenAsync(inputPath, (msg, level) => log(msg, level), ct),
            ".cci" or ".3ds" => await CciSource.OpenAsync(inputPath, keyStore, (msg, level) => log(msg, level), ct),
            _ => throw new NotSupportedException($"지원하지 않는 파일 형식: {ext}")
        };
    }
}