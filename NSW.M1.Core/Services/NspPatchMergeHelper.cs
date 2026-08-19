using Common;
using Patch.Core.Services;
using Path = System.IO.Path;

namespace NSW.M1.Core.Services;

public static class NspPatchMergeHelper
{
    public static readonly string[] PatchMarkerExtensions = [".xdelta", ".xdelta3", ".ips"];

    public static int ApplyArchiveSubDir(IArchivePatchSource archive, string folderName, string targetDir, IProgress<(int pct, string label)> progress, Action<string, LogLevel> log, string label)
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
            log($"  {label} 교체: {rel}", LogLevel.Info);

            count++;
        }

        if (count > 0)
            log($"  {label} 교체 완료: {count}개 파일", LogLevel.Ok);

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