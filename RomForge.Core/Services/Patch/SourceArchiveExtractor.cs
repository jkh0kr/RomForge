using CHD.Core.Services;
using Common;
using RomForge.Core.Models.Patch;
using SharpCompress.Archives;
using System.IO;

namespace RomForge.Core.Services.Patch;

public static class SourceArchiveExtractor
{
    private static readonly string[] SupportedExtensions = [".zip", ".7z", ".rar"];

    private const long TinySizeThresholdBytes = 64 * 1024;

    private static readonly HashSet<string> RomLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nsp", ".nsz", ".xci", ".xcz",
        ".3ds", ".cci", ".cxi", ".cia", ".app", ".srl",
        ".nds", ".dsi", ".ids",
        ".wud", ".wux", ".wua", ".wup", ".rpx",
        ".gcm", ".gcz", ".wbfs", ".wia", ".rvz",
        ".pbp", ".cso",
        ".iso", ".bin", ".cue", ".gdi", ".cdi", ".chd", ".nrg", ".mdf", ".mds", ".img", ".ccd", ".sub", ".cdr", ".toc",
        ".nes", ".fds", ".unf", ".unif",
        ".sfc", ".smc", ".fig", ".swc",
        ".n64", ".z64", ".v64", ".ndd",
        ".gb", ".gbc", ".gba",
        ".sms", ".gg", ".md", ".gen", ".smd", ".32x", ".sg",
        ".neo", ".ngp", ".ngc",
        ".a26", ".a52", ".a78", ".lnx", ".j64",
        ".col",
        ".pce", ".sgx",
        ".ws", ".wsc",
        ".xbe", ".xex", ".god",
        ".vpk", ".pkg"
    };

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".url", ".ini", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".md5", ".sfv", ".log", ".pdf", ".doc", ".docx"];

    public static bool IsArchivePath(string? path) =>
        !string.IsNullOrEmpty(path) && SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static Task<ArchiveExtractResult> ExtractAsync(string archivePath, string extractDir, IProgress<ProgressInfo> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);

            var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key is not null).ToList();

            if (entries.Count == 0)
                throw new InvalidOperationException("압축 파일에 항목이 없습니다.");

            long totalBytes = entries.Sum(e => e.Size);
            long writtenTotal = 0;

            var reporter = new ProgressReporter("원본 압축 해제 중...", string.Empty, totalBytes, progress);
            var report = reporter.CreateAction();

            var extracted = new List<(string Path, long Size)>();
            byte[] buffer = new byte[81920];

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                string relativePath = entry.Key!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                string destPath = Path.Combine(extractDir, relativePath);

                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                using (var entryStream = entry.OpenEntryStream())
                using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                {
                    int bytesRead;
                    while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        destStream.Write(buffer, 0, bytesRead);
                        writtenTotal += bytesRead;

                        if (totalBytes > 0)
                            report(writtenTotal, totalBytes);
                    }
                }

                extracted.Add((destPath, entry.Size));
            }

            reporter.ForceReport();

            return ResolveSourceFile(extracted);
        }, ct);

    private static ArchiveExtractResult ResolveSourceFile(List<(string Path, long Size)> extracted)
    {
        var cue = extracted.FirstOrDefault(e =>
            string.Equals(Path.GetExtension(e.Path), ".cue", StringComparison.OrdinalIgnoreCase));

        if (cue.Path is not null)
        {
            var referencedBins = ConversionSource.ParseBinsFromCue(cue.Path);

            foreach (var bin in referencedBins)
            {
                var match = extracted.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.Path), Path.GetFileName(bin), StringComparison.OrdinalIgnoreCase));

                if (match.Path is not null)
                    return new ArchiveExtractResult { ResolvedPath = match.Path };
            }

            throw new InvalidOperationException("CUE 파일이 참조하는 BIN 파일을 압축 안에서 찾을 수 없습니다.");
        }

        var candidates = extracted.Where(e => RomLikeExtensions.Contains(Path.GetExtension(e.Path))).ToList();

        if (candidates.Count == 0)
        {
            candidates = [.. extracted
                .Where(e => !IgnoredExtensions.Contains(Path.GetExtension(e.Path), StringComparer.OrdinalIgnoreCase))
                .Where(e => e.Size >= TinySizeThresholdBytes)];
        }

        if (candidates.Count == 0)
        {
            candidates = [.. extracted.Where(e => !IgnoredExtensions.Contains(Path.GetExtension(e.Path), StringComparer.OrdinalIgnoreCase))];
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("압축 안에서 패치 대상으로 볼 만한 파일을 찾을 수 없습니다.");

        if (candidates.Count == 1)
            return new ArchiveExtractResult { ResolvedPath = candidates[0].Path };

        return new ArchiveExtractResult
        {
            Candidates = [.. candidates.Select(c => new ArchiveCandidate(c.Path, c.Size))]
        };
    }
}