using Common;
using NSW.M1.Core.Models;
using Patch.Core;
using Patch.Core.Formats;
using Patch.Core.Services;
using Path = System.IO.Path;

namespace NSW.M1.Core.Services;

public static class NspPatchApplier
{
    private static readonly string[] PatchMarkerExtensions = [".xdelta", ".xdelta3", ".ips"];

    private readonly record struct XdeltaCandidate(string TargetFileName, string? AbsoluteExactPath, PatchFileRef PatchRef, string DisplayName);

    public static void ApplyPatch(string patchPath, UnpackResult unpackResult, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, string? patchPassword = null)
    {
        string exefsDir = unpackResult.ExefsDirs.GetValueOrDefault((byte)0, string.Empty);
        string romfsDir = unpackResult.RomfsDirs.GetValueOrDefault((byte)0, string.Empty);
        int matchedCount = 0;

        if (ArchivePatchSourceFactory.IsArchivePath(patchPath))
        {
            using var archive = ArchivePatchSourceFactory.Open(patchPath, patchPassword);

            matchedCount += ApplyArchiveSubDir(archive, "exefs", exefsDir, progress, log, "한글패치 ExeFS");
            matchedCount += ApplyArchiveSubDir(archive, "romfs", romfsDir, progress, log, "한글패치 RomFS");
            matchedCount += ApplyArchiveXdelta(archive, exefsDir, romfsDir, progress, log);
            matchedCount += ApplyArchiveExefsPatches(archive, exefsDir, progress, log);
        }
        else
        {
            string? patchExefs = PatchFolderResolver.FindSubDir(patchPath, "exefs");
            string? patchRomfs = PatchFolderResolver.FindSubDir(patchPath, "romfs");

            if (patchExefs != null)
            {
                progress.Report((-1, "한글패치 ExeFS 병합 중..."));
                log($"  한글패치 ExeFS 병합: {patchExefs}", LogLevel.Info);
                matchedCount += MergeDirectory(patchExefs, exefsDir, log);
            }
            if (patchRomfs != null)
            {
                progress.Report((-1, "한글패치 RomFS 병합 중..."));
                log($"  한글패치 RomFS 병합: {patchRomfs}", LogLevel.Info);
                matchedCount += MergeDirectory(patchRomfs, romfsDir, log);
            }

            if (Directory.Exists(patchPath))
            {
                matchedCount += ApplyFolderXdelta(patchPath, exefsDir, romfsDir, progress, log);
                matchedCount += ApplyFolderExefsPatches(patchPath, exefsDir, progress, log);
            }
        }

        if (matchedCount == 0)
            log("  패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
    }

    public static void ApplyDlcPatch(string patchPath, string titleIdStr, string romfsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, string? patchPassword = null)
    {
        int matchedCount = 0;

        if (ArchivePatchSourceFactory.IsArchivePath(patchPath))
        {
            using var archive = ArchivePatchSourceFactory.Open(patchPath, patchPassword);

            matchedCount += ApplyArchiveSubDir(archive, "romfs", romfsDir, progress, log, $"DLC 패치 RomFS ({titleIdStr})");
            matchedCount += ApplyArchiveXdeltaDlc(archive, romfsDir, titleIdStr, progress, log);
        }
        else
        {
            if (!Directory.Exists(patchPath))
                return;

            string? patchRomfs = PatchFolderResolver.FindSubDir(patchPath, "romfs");

            if (patchRomfs != null)
            {
                progress.Report((-1, $"DLC 패치 RomFS 병합 중... ({titleIdStr})"));
                log($"  DLC 패치 RomFS 병합: {patchRomfs}", LogLevel.Info);
                matchedCount += MergeDirectory(patchRomfs, romfsDir, log);
            }

            matchedCount += ApplyFolderXdeltaDlc(patchPath, romfsDir, titleIdStr, progress, log);
        }

        if (matchedCount == 0)
            log("  패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
    }

    private static IEnumerable<string> FindTargetsByContains(string? dir, string targetFileName)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            yield break;

        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (targetFileName.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))
                yield return f;
        }
    }

    private static int ApplyXdeltaCandidates(List<XdeltaCandidate> candidates, string? exefsDir, string romfsDir, string displayRoot, bool isDlc, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        int count = 0;
        int successCount = 0;
        string label = isDlc ? "DLC xdelta" : "xdelta";

        foreach (var candidate in candidates)
        {
            var targetFiles = new List<string>();

            if (candidate.AbsoluteExactPath != null && File.Exists(candidate.AbsoluteExactPath))
                targetFiles.Add(candidate.AbsoluteExactPath);
            else
            {
                targetFiles.AddRange(FindTargetsByContains(exefsDir, candidate.TargetFileName));
                targetFiles.AddRange(FindTargetsByContains(romfsDir, candidate.TargetFileName));
            }

            if (targetFiles.Count == 0)
            {
                log($"  ⚠️ {label} 대상 원본 파일을 찾을 수 없음: {candidate.TargetFileName}", LogLevel.Info);
                continue;
            }

            using var tempPatch = candidate.PatchRef.MaterializeAsTempFile(".xdelta");

            foreach (var targetPath in targetFiles.Distinct())
            {
                count++;

                if (ApplyXdeltaToTarget(tempPatch.Path, targetPath, displayRoot, progress, log, isDlc: isDlc, displayName: candidate.DisplayName))
                    successCount++;
            }
        }

        if (count > 0)
            log($"  {label} 패치 완료 수: {successCount}개 / {count}개", LogLevel.Ok);

        return count;
    }

    private static int ApplyFolderXdelta(string patchPath, string exefsDir, string romfsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var xdeltaFiles = Directory.EnumerateFiles(patchPath, "*.xdelta", SearchOption.AllDirectories)
                                   .OrderBy(f => f)
                                   .ToList();

        if (xdeltaFiles.Count == 0)
            return 0;

        progress.Report((-1, "xdelta 바이너리 패치 적용 중..."));
        log($"  발견된 xdelta 패치 수: {xdeltaFiles.Count}개", LogLevel.Info);

        string unpackedRoot = Path.GetDirectoryName(exefsDir)!;
        var candidates = new List<XdeltaCandidate>();

        foreach (var xdeltaPath in xdeltaFiles)
        {
            string targetFileName = Path.GetFileNameWithoutExtension(xdeltaPath);
            string relativePath = Path.GetRelativePath(patchPath, xdeltaPath);
            string relativeTargetKey = Path.Combine(Path.GetDirectoryName(relativePath) ?? string.Empty, targetFileName);
            string absoluteExactPath = Path.Combine(unpackedRoot, relativeTargetKey);

            candidates.Add(new XdeltaCandidate(targetFileName, absoluteExactPath, PatchFileRef.FromDisk(xdeltaPath), Path.GetFileName(xdeltaPath)));
        }

        return ApplyXdeltaCandidates(candidates, exefsDir, romfsDir, unpackedRoot, false, progress, log);
    }

    private static int ApplyFolderXdeltaDlc(string patchPath, string romfsDir, string titleIdStr, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var xdeltaFiles = Directory.EnumerateFiles(patchPath, "*.xdelta", SearchOption.AllDirectories)
                                   .OrderBy(f => f)
                                   .ToList();

        if (xdeltaFiles.Count == 0)
            return 0;

        progress.Report((-1, $"DLC xdelta 패치 적용 중... ({titleIdStr})"));
        log($"  발견된 DLC xdelta 패치 수: {xdeltaFiles.Count}개", LogLevel.Info);

        var candidates = xdeltaFiles
            .Select(xdeltaPath => new XdeltaCandidate(
                Path.GetFileNameWithoutExtension(xdeltaPath),
                null,
                PatchFileRef.FromDisk(xdeltaPath),
                Path.GetFileName(xdeltaPath)))
            .ToList();

        return ApplyXdeltaCandidates(candidates, null, romfsDir, romfsDir, true, progress, log);
    }

    private static int ApplyArchiveSubDir(IArchivePatchSource archive, string folderName, string targetDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, string label)
    {
        string? prefix = ArchivePatchFolderResolver.FindSubDir(archive.EntryPaths, folderName);

        if (prefix == null)
            return 0;

        progress.Report((-1, $"{label} 병합 중..."));
        log($"  {label} 병합(압축파일): {prefix}", LogLevel.Info);

        int count = 0;

        foreach (string key in archive.EntryPaths)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rel = key[prefix.Length..];

            if (rel.Length == 0 || PatchMarkerExtensions.Any(ext => rel.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                continue;

            var entry = archive.FindEntry(key);

            if (entry == null)
                continue;

            string dest = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            using var src = entry.Open();
            using var dst = File.Create(dest);

            src.CopyTo(dst);

            log($"  {label} 교체: {rel}", LogLevel.Ok);

            count++;
        }

        if (count > 0)
            log($"  {label} 교체 완료: {count}개 파일", LogLevel.Ok);

        return count;
    }

    private static (string EntryName, string TargetFileName) ParseArchiveXdeltaKey(string key)
    {
        string entryName = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
        string targetFileName = Path.GetFileNameWithoutExtension(entryName);

        return (entryName, targetFileName);
    }

    private static int ApplyArchiveXdelta(IArchivePatchSource archive, string exefsDir, string romfsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var xdeltaKeys = archive.EntryPaths
            .Where(k => k.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .ToList();

        if (xdeltaKeys.Count == 0)
            return 0;

        progress.Report((-1, "xdelta 바이너리 패치 적용 중..."));
        log($"  발견된 xdelta 패치 수: {xdeltaKeys.Count}개", LogLevel.Info);

        string unpackedRoot = Path.GetDirectoryName(exefsDir) is { Length: > 0 } d ? d : Path.GetDirectoryName(romfsDir) ?? string.Empty;
        var candidates = new List<XdeltaCandidate>();

        foreach (string key in xdeltaKeys)
        {
            var (entryName, targetFileName) = ParseArchiveXdeltaKey(key);
            int lastSlash = key.LastIndexOf('/');
            string relDir = lastSlash < 0 ? "" : key[..lastSlash];
            string relativeTargetKey = relDir.Length == 0 ? targetFileName : $"{relDir}/{targetFileName}";
            string absoluteExactPath = Path.Combine(unpackedRoot, relativeTargetKey.Replace('/', Path.DirectorySeparatorChar));

            var entry = archive.FindEntry(key);

            if (entry == null)
                continue;

            candidates.Add(new XdeltaCandidate(targetFileName, absoluteExactPath, PatchFileRef.FromArchiveEntry(entry), entryName));
        }

        return ApplyXdeltaCandidates(candidates, exefsDir, romfsDir, unpackedRoot, false, progress, log);
    }

    private static int ApplyArchiveXdeltaDlc(IArchivePatchSource archive, string romfsDir, string titleIdStr, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var xdeltaKeys = archive.EntryPaths
            .Where(k => k.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .ToList();

        if (xdeltaKeys.Count == 0)
            return 0;

        progress.Report((-1, $"DLC xdelta 패치 적용 중... ({titleIdStr})"));
        log($"  발견된 DLC xdelta 패치 수: {xdeltaKeys.Count}개", LogLevel.Info);

        var candidates = new List<XdeltaCandidate>();

        foreach (string key in xdeltaKeys)
        {
            var (entryName, targetFileName) = ParseArchiveXdeltaKey(key);
            var entry = archive.FindEntry(key);

            if (entry == null)
                continue;

            candidates.Add(new XdeltaCandidate(targetFileName, null, PatchFileRef.FromArchiveEntry(entry), entryName));
        }

        return ApplyXdeltaCandidates(candidates, null, romfsDir, romfsDir, true, progress, log);
    }

    private static bool ApplyXdeltaToTarget(string xdeltaPath, string targetPath, string displayRoot, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, bool isDlc = false, string? displayName = null)
    {
        string displayPath = Path.GetRelativePath(displayRoot, targetPath);
        string prefix = isDlc ? "DLC xdelta" : "xdelta";
        string shownName = displayName ?? Path.GetFileName(xdeltaPath);

        log($"  {prefix} 패치 적용: {shownName} ➡️ {displayPath}", LogLevel.Info);

        string tempOutPath = targetPath + ".patched";

        try
        {
            var wrapper = new Progress<ProgressInfo>(p =>
            {
                int currentStep = 80 + (int)(p.Percent * 0.1);

                if (currentStep > 80)
                    progress?.Report((currentStep, string.Empty));
            });

            Xdelta3.ApplyPatch(targetPath, Path.GetFullPath(xdeltaPath), tempOutPath, wrapper);

            if (!File.Exists(tempOutPath))
            {
                log($"  ❌ {prefix} 패치 실패 ({shownName}): 출력 파일이 생성되지 않았습니다.", LogLevel.Error);
                return false;
            }

            File.Delete(targetPath);
            File.Move(tempOutPath, targetPath);

            return true;
        }
        catch (Exception ex)
        {
            log($"  ❌ {prefix} 패치 실패 ({shownName}): {ex.Message}", LogLevel.Error);

            if (File.Exists(tempOutPath))
                File.Delete(tempOutPath);

            return false;
        }
    }

    private static (string BuildId, Func<byte[]> ReadIps) MakeArchiveIpsItem(IArchivePatchSource archive, string key)
    {
        string buildId = Path.GetFileNameWithoutExtension(key).TrimEnd('0');

        byte[] Read()
        {
            var entry = archive.FindEntry(key)!;
            using var src = entry.Open();
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            return ms.ToArray();
        }

        return (buildId, Read);
    }

    private static int ApplyFolderExefsPatches(string patchPath, string exefsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        if (string.IsNullOrEmpty(exefsDir) || !Directory.Exists(exefsDir) || !Directory.Exists(patchPath))
            return 0;

        var openedArchives = new List<IArchivePatchSource>();
        var items = new List<(string BuildId, Func<byte[]> ReadIps)>();

        try
        {
            foreach (var f in Directory.EnumerateFiles(patchPath, "*.ips", SearchOption.AllDirectories))
            {
                string path = f;
                items.Add((Path.GetFileNameWithoutExtension(path).TrimEnd('0'), () => File.ReadAllBytes(path)));
            }

            var archivePaths = Directory.EnumerateFiles(patchPath, "*.zip", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(patchPath, "*.7z", SearchOption.AllDirectories));

            foreach (var archivePath in archivePaths)
            {
                IArchivePatchSource archive;

                try
                {
                    archive = ArchivePatchSourceFactory.Open(archivePath);
                }
                catch (Exception ex)
                {
                    log($"  ⚠️ 압축파일을 열 수 없음(암호 걸림/손상 등): {Path.GetFileName(archivePath)} — {ex.Message}", LogLevel.Info);
                    continue;
                }

                openedArchives.Add(archive);

                foreach (var key in archive.EntryPaths.Where(k => k.EndsWith(".ips", StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(MakeArchiveIpsItem(archive, key));
                }
            }

            if (items.Count == 0)
                return 0;

            log($"  exefs_patches용 .ips 발견: {items.Count}개", LogLevel.Info);

            return ApplyExefsPatchesCore(items, exefsDir, progress, log);
        }
        finally
        {
            foreach (var a in openedArchives)
                a.Dispose();
        }
    }

    private static int ApplyArchiveExefsPatches(IArchivePatchSource archive, string exefsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var ipsKeys = archive.EntryPaths.Where(k => k.EndsWith(".ips", StringComparison.OrdinalIgnoreCase)).ToList();

        if (ipsKeys.Count == 0)
            return 0;

        var items = ipsKeys.Select(k => MakeArchiveIpsItem(archive, k));

        return ApplyExefsPatchesCore(items, exefsDir, progress, log);
    }

    private static int ApplyExefsPatchesCore(IEnumerable<(string BuildId, Func<byte[]> ReadIps)> ipsItems, string exefsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var buildIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nsoPath in Directory.EnumerateFiles(exefsDir))
        {
            if (Path.GetFileName(nsoPath).Equals("main.npdm", StringComparison.OrdinalIgnoreCase))
                continue;

            byte[] data = File.ReadAllBytes(nsoPath);

            if (!NsoTool.IsNso(data))
                continue;

            buildIdMap[NsoTool.GetBuildIdHex(data)] = nsoPath;
        }

        int count = 0;

        foreach (var (buildId, readIps) in ipsItems)
        {
            if (!buildIdMap.TryGetValue(buildId, out var targetNso))
            {
                log($"  ⚠️ exefs_patches 대상 NSO를 찾을 수 없음 (build id 불일치): {buildId}", LogLevel.Info);
                continue;
            }

            progress.Report((-1, $"exefs_patches 적용 중... ({Path.GetFileName(targetNso)})"));

            byte[] nso = File.ReadAllBytes(targetNso);
            uint originalFlags = BitConverter.ToUInt32(nso, 0x0C);
            bool wasCompressed = NsoTool.IsCompressed(nso);
            byte[] plain = wasCompressed ? NsoTool.DecompressToPlain(nso) : nso;
            byte[] patched = UniversalPatcher.ApplyPatchAsync(plain, readIps()).GetAwaiter().GetResult();
            byte[] final = wasCompressed ? NsoTool.RecompressFromPlain(patched, originalFlags) : patched;

            File.WriteAllBytes(targetNso, final);

            log($"  exefs_patches 적용 완료: {Path.GetFileName(targetNso)} ⬅️ {buildId} (재압축: {wasCompressed})", LogLevel.Ok);
            count++;
        }

        return count;
    }

    public static int MergeDirectory(string srcDir, string dstDir, Action<string, LogLevel>? log = null)
    {
        Directory.CreateDirectory(dstDir);

        int count = 0;

        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            if (PatchMarkerExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                continue;

            string rel = Path.GetRelativePath(srcDir, file);
            string dest = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);

            log?.Invoke($"  교체: {rel}", LogLevel.Ok);

            count++;
        }

        if (count > 0)
            log?.Invoke($"  교체 완료: {count}개 파일 ({srcDir})", LogLevel.Ok);

        return count;
    }
}