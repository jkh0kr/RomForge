using Common;
using NSW.M1.Core.Models;
using Patch.Core.Services;

namespace NSW.M1.Core.Services;

public static class NspPatchApplier
{
    public static void ApplyPatch(string patchPath, UnpackResult unpackResult, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, string? patchPassword = null)
    {
        string exefsDir = unpackResult.ExefsDirs.GetValueOrDefault((byte)0, string.Empty);
        string romfsDir = unpackResult.RomfsDirs.GetValueOrDefault((byte)0, string.Empty);
        int matchedCount = 0;

        if (ArchivePatchSourceFactory.IsArchivePath(patchPath))
        {
            using var archive = ArchivePatchSourceFactory.Open(patchPath, patchPassword);

            matchedCount += NspPatchMergeHelper.ApplyArchiveSubDir(archive, "exefs", exefsDir, progress, log, "한글패치 ExeFS");
            matchedCount += NspPatchMergeHelper.ApplyArchiveSubDir(archive, "romfs", romfsDir, progress, log, "한글패치 RomFS");
            matchedCount += NspXdeltaPatcher.ApplyArchiveXdelta(archive, exefsDir, romfsDir, progress, log);
            matchedCount += NspExefsPatchApplier.ApplyArchiveExefsPatches(archive, exefsDir, progress, log);
        }
        else
        {
            string? patchExefs = PatchFolderResolver.FindSubDir(patchPath, "exefs");
            string? patchRomfs = PatchFolderResolver.FindSubDir(patchPath, "romfs");

            if (patchExefs != null)
            {
                progress.Report((-1, "한글패치 ExeFS 병합 중..."));
                log($"  한글패치 ExeFS 병합: {patchExefs}", LogLevel.Info);
                matchedCount += NspPatchMergeHelper.MergeDirectory(patchExefs, exefsDir, log);
            }
            if (patchRomfs != null)
            {
                progress.Report((-1, "한글패치 RomFS 병합 중..."));
                log($"  한글패치 RomFS 병합: {patchRomfs}", LogLevel.Info);
                matchedCount += NspPatchMergeHelper.MergeDirectory(patchRomfs, romfsDir, log);
            }

            if (Directory.Exists(patchPath))
            {
                matchedCount += NspXdeltaPatcher.ApplyFolderXdelta(patchPath, exefsDir, romfsDir, progress, log);
                matchedCount += NspExefsPatchApplier.ApplyFolderExefsPatches(patchPath, exefsDir, progress, log);
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

            matchedCount += NspPatchMergeHelper.ApplyArchiveSubDir(archive, "romfs", romfsDir, progress, log, $"DLC 패치 RomFS ({titleIdStr})");
            matchedCount += NspXdeltaPatcher.ApplyArchiveXdeltaDlc(archive, romfsDir, titleIdStr, progress, log);
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
                matchedCount += NspPatchMergeHelper.MergeDirectory(patchRomfs, romfsDir, log);
            }

            matchedCount += NspXdeltaPatcher.ApplyFolderXdeltaDlc(patchPath, romfsDir, titleIdStr, progress, log);
        }

        if (matchedCount == 0)
            log("  패치 대상 파일이 존재하지 않습니다.", LogLevel.Error);
    }
}