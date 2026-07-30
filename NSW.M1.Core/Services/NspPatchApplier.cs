using Common;
using NSW.M1.Core.Models;
using Patch.Core.Formats;
using Patch.Core.Services;
using System.IO.Compression;
using Path = System.IO.Path;

namespace NSW.M1.Core.Services;

public static class NspPatchApplier
{
    public static void ApplyPatch(string patchPath, UnpackResult unpackResult, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        string exefsDir = unpackResult.ExefsDirs.GetValueOrDefault((byte)0, string.Empty);
        string romfsDir = unpackResult.RomfsDirs.GetValueOrDefault((byte)0, string.Empty);
        int matchedCount = 0;

        if (ZipPatchSource.IsZipPath(patchPath))
        {
            using var archive = ZipFile.OpenRead(patchPath);

            matchedCount += ApplyZipSubDir(archive, "exefs", exefsDir, progress, log, "한글패치 ExeFS");
            matchedCount += ApplyZipSubDir(archive, "romfs", romfsDir, progress, log, "한글패치 RomFS");
            matchedCount += ApplyZipXdelta(archive, exefsDir, romfsDir, progress, log);
        }
        else
        {
            string? patchExefs = PatchFolderResolver.FindSubDir(patchPath, "exefs");
            string? patchRomfs = PatchFolderResolver.FindSubDir(patchPath, "romfs");

            if (patchExefs != null)
            {
                progress.Report((-1, "한글패치 ExeFS 병합 중..."));
                log($"  한글패치 ExeFS 병합: {patchExefs}", LogLevel.Info);
                matchedCount += MergeDirectory(patchExefs, exefsDir);
            }
            if (patchRomfs != null)
            {
                progress.Report((-1, "한글패치 RomFS 병합 중..."));
                log($"  한글패치 RomFS 병합: {patchRomfs}", LogLevel.Info);
                matchedCount += MergeDirectory(patchRomfs, romfsDir);
            }

            if (Directory.Exists(patchPath))
            {
                var xdeltaFiles = Directory.EnumerateFiles(patchPath, "*.xdelta", SearchOption.AllDirectories)
                                           .OrderBy(f => f)
                                           .ToList();

                if (xdeltaFiles.Count > 0)
                {
                    progress.Report((-1, "xdelta 바이너리 패치 적용 중..."));
                    log($"  발견된 xdelta 패치 수: {xdeltaFiles.Count}개", LogLevel.Info);

                    string unpackedRoot = Path.GetDirectoryName(exefsDir)!;

                    foreach (var xdeltaPath in xdeltaFiles)
                    {
                        string targetFileName = Path.GetFileNameWithoutExtension(xdeltaPath);
                        string relativePath = Path.GetRelativePath(patchPath, xdeltaPath);
                        string relativeTargetKey = Path.Combine(Path.GetDirectoryName(relativePath) ?? string.Empty, targetFileName);
                        var targetFiles = new List<string>();
                        string absoluteExactPath = Path.Combine(unpackedRoot, relativeTargetKey);

                        if (File.Exists(absoluteExactPath))
                            targetFiles.Add(absoluteExactPath);
                        else
                        {
                            if (!string.IsNullOrEmpty(exefsDir))
                                targetFiles.AddRange(Directory.EnumerateFiles(exefsDir, targetFileName, SearchOption.AllDirectories));

                            if (!string.IsNullOrEmpty(romfsDir))
                                targetFiles.AddRange(Directory.EnumerateFiles(romfsDir, targetFileName, SearchOption.AllDirectories));
                        }

                        if (targetFiles.Count > 0)
                        {
                            foreach (var targetPath in targetFiles.Distinct())
                            {
                                ApplyXdeltaToTarget(xdeltaPath, targetPath, unpackedRoot, progress, log);
                                matchedCount++;
                            }
                        }
                        else
                            log($"  ⚠️ xdelta 대상 원본 파일을 찾을 수 없음: {targetFileName}", LogLevel.Info);
                    }
                }
            }
        }

        if (matchedCount == 0)
            log("  패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
    }

    public static void ApplyDlcPatch(string patchPath, string titleIdStr, string romfsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        int matchedCount = 0;

        if (ZipPatchSource.IsZipPath(patchPath))
        {
            using var archive = ZipFile.OpenRead(patchPath);

            matchedCount += ApplyZipSubDir(archive, "romfs", romfsDir, progress, log, $"DLC 패치 RomFS ({titleIdStr})");
            matchedCount += ApplyZipXdeltaDlc(archive, romfsDir, titleIdStr, progress, log);
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
                matchedCount += MergeDirectory(patchRomfs, romfsDir);
            }

            var xdeltaFiles = Directory.EnumerateFiles(patchPath, "*.xdelta", SearchOption.AllDirectories)
                                       .OrderBy(f => f)
                                       .ToList();

            if (xdeltaFiles.Count > 0)
            {
                progress.Report((-1, $"DLC xdelta 패치 적용 중... ({titleIdStr})"));
                log($"  발견된 DLC xdelta 패치 수: {xdeltaFiles.Count}개", LogLevel.Info);

                foreach (var xdeltaPath in xdeltaFiles)
                {
                    string targetFileName = Path.GetFileNameWithoutExtension(xdeltaPath);
                    var targetFiles = Directory.EnumerateFiles(romfsDir, targetFileName, SearchOption.AllDirectories).ToList();

                    if (targetFiles.Count == 0)
                    {
                        log($"  ⚠️ DLC xdelta 대상 원본 파일을 찾을 수 없음: {targetFileName}", LogLevel.Info);
                        continue;
                    }

                    foreach (var targetPath in targetFiles)
                    {
                        ApplyXdeltaToTarget(xdeltaPath, targetPath, romfsDir, progress, log, isDlc: true);
                        matchedCount++;
                    }
                }
            }
        }

        if (matchedCount == 0)
            log("  패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
    }

    private static int ApplyZipSubDir(ZipArchive archive, string folderName, string targetDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, string label)
    {
        string? prefix = ZipPatchSource.FindSubDir(archive, folderName);

        if (prefix == null)
            return 0;

        progress.Report((-1, $"{label} 병합 중..."));
        log($"  {label} 병합(zip): {prefix}", LogLevel.Info);

        int count = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            string key = entry.FullName.Replace('\\', '/');

            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rel = key[prefix.Length..];

            if (rel.Length == 0 || rel.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
                continue;

            string dest = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            using var src = entry.Open();
            using var dst = File.Create(dest);

            src.CopyTo(dst);

            count++;
        }

        return count;
    }

    private static int ApplyZipXdelta(ZipArchive archive, string exefsDir, string romfsDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var xdeltaEntries = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && e.Name.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName)
            .ToList();

        if (xdeltaEntries.Count == 0)
            return 0;

        progress.Report((-1, "xdelta 바이너리 패치 적용 중..."));
        log($"  발견된 xdelta 패치 수: {xdeltaEntries.Count}개", LogLevel.Info);

        string unpackedRoot = Path.GetDirectoryName(exefsDir) is { Length: > 0 } d ? d : Path.GetDirectoryName(romfsDir) ?? string.Empty;
        int count = 0;

        foreach (var entry in xdeltaEntries)
        {
            string entryPath = entry.FullName.Replace('\\', '/');
            string targetFileName = Path.GetFileNameWithoutExtension(entry.Name);
            int lastSlash = entryPath.LastIndexOf('/');
            string relDir = lastSlash < 0 ? "" : entryPath[..lastSlash];
            string relativeTargetKey = relDir.Length == 0 ? targetFileName : $"{relDir}/{targetFileName}";
            string absoluteExactPath = Path.Combine(unpackedRoot, relativeTargetKey.Replace('/', Path.DirectorySeparatorChar));

            var targetFiles = new List<string>();

            if (File.Exists(absoluteExactPath))
                targetFiles.Add(absoluteExactPath);
            else
            {
                if (!string.IsNullOrEmpty(exefsDir) && Directory.Exists(exefsDir))
                    targetFiles.AddRange(Directory.EnumerateFiles(exefsDir, targetFileName, SearchOption.AllDirectories));

                if (!string.IsNullOrEmpty(romfsDir) && Directory.Exists(romfsDir))
                    targetFiles.AddRange(Directory.EnumerateFiles(romfsDir, targetFileName, SearchOption.AllDirectories));
            }

            if (targetFiles.Count == 0)
            {
                log($"  ⚠️ xdelta 대상 원본 파일을 찾을 수 없음: {targetFileName}", LogLevel.Info);
                continue;
            }

            var capturedEntry = entry;
            var patchRef = PatchFileRef.FromZip(() => capturedEntry.Open(), capturedEntry.Length);

            using var tempPatch = patchRef.MaterializeAsTempFile(".xdelta");

            foreach (var targetPath in targetFiles.Distinct())
            {
                ApplyXdeltaToTarget(tempPatch.Path, targetPath, unpackedRoot, progress, log, displayName: entry.Name);
                count++;
            }
        }

        return count;
    }

    private static int ApplyZipXdeltaDlc(ZipArchive archive, string romfsDir, string titleIdStr, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log)
    {
        var xdeltaEntries = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && e.Name.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName)
            .ToList();

        if (xdeltaEntries.Count == 0)
            return 0;

        progress.Report((-1, $"DLC xdelta 패치 적용 중... ({titleIdStr})"));
        log($"  발견된 DLC xdelta 패치 수: {xdeltaEntries.Count}개", LogLevel.Info);

        int count = 0;

        foreach (var entry in xdeltaEntries)
        {
            string targetFileName = Path.GetFileNameWithoutExtension(entry.Name);
            var targetFiles = Directory.EnumerateFiles(romfsDir, targetFileName, SearchOption.AllDirectories).ToList();

            if (targetFiles.Count == 0)
            {
                log($"  ⚠️ DLC xdelta 대상 원본 파일을 찾을 수 없음: {targetFileName}", LogLevel.Info);
                continue;
            }

            var capturedEntry = entry;
            var patchRef = PatchFileRef.FromZip(() => capturedEntry.Open(), capturedEntry.Length);

            using var tempPatch = patchRef.MaterializeAsTempFile(".xdelta");

            foreach (var targetPath in targetFiles)
            {
                ApplyXdeltaToTarget(tempPatch.Path, targetPath, romfsDir, progress, log, isDlc: true, displayName: entry.Name);
                count++;
            }
        }

        return count;
    }

    private static void ApplyXdeltaToTarget(string xdeltaPath, string targetPath, string displayRoot, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, bool isDlc = false, string? displayName = null)
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

            if (File.Exists(tempOutPath))
            {
                File.Delete(targetPath);
                File.Move(tempOutPath, targetPath);
            }
        }
        catch (Exception ex)
        {
            log($"  ❌ {prefix} 패치 실패 ({shownName}): {ex.Message}", LogLevel.Error);

            if (File.Exists(tempOutPath)) 
                File.Delete(tempOutPath);
        }
    }

    public static int MergeDirectory(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);

        int count = 0;

        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
                continue;

            string rel = Path.GetRelativePath(srcDir, file);
            string dest = Path.Combine(dstDir, rel);

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            count++;
        }

        return count;
    }
}