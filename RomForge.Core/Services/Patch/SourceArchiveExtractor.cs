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

    public static Task<ArchiveExtractResult> AnalyzeAndExtractAsync(string archivePath, string extractDir, IProgress<ProgressInfo> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);

            var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key is not null).ToList();

            if (entries.Count == 0)
                throw new InvalidOperationException("압축 파일에 항목이 없습니다.");

            var cueEntries = entries.Where(e =>
                string.Equals(Path.GetExtension(e.Key), ".cue", StringComparison.OrdinalIgnoreCase)).ToList();

            if (cueEntries.Count == 1)
                return ResolveCue(cueEntries[0], entries, extractDir, progress, ct);

            if (cueEntries.Count > 1)
            {
                return new ArchiveExtractResult
                {
                    Candidates = [.. cueEntries.Select(e => new ArchiveCandidate(e.Key!, e.Size))]
                };
            }

            var candidates = entries.Where(e => RomLikeExtensions.Contains(Path.GetExtension(e.Key))).ToList();

            if (candidates.Count == 0)
            {
                candidates = entries
                    .Where(e => !IgnoredExtensions.Contains(Path.GetExtension(e.Key), StringComparer.OrdinalIgnoreCase))
                    .Where(e => e.Size >= TinySizeThresholdBytes)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                candidates = entries
                    .Where(e => !IgnoredExtensions.Contains(Path.GetExtension(e.Key), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException("압축 안에서 패치 대상으로 볼 만한 파일을 찾을 수 없습니다.");

            if (candidates.Count > 1)
            {
                return new ArchiveExtractResult
                {
                    Candidates = [.. candidates.Select(e => new ArchiveCandidate(e.Key!, e.Size))]
                };
            }

            var extracted = ExtractEntries(candidates, extractDir, progress, ct);

            return new ArchiveExtractResult { ResolvedPath = extracted[candidates[0].Key!] };
        }, ct);

    public static Task<string> ExtractCandidateAsync(string archivePath, string extractDir, string entryKey, IProgress<ProgressInfo> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);

            var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key is not null).ToList();

            var entry = entries.FirstOrDefault(e => string.Equals(e.Key, entryKey, StringComparison.Ordinal));

            if (entry is null)
                throw new InvalidOperationException("선택한 파일을 압축 안에서 찾을 수 없습니다.");

            if (string.Equals(Path.GetExtension(entry.Key), ".cue", StringComparison.OrdinalIgnoreCase))
            {
                var cueResult = ResolveCue(entry, entries, extractDir, progress, ct);
                return cueResult.ResolvedPath!;
            }

            var extracted = ExtractEntries([entry], extractDir, progress, ct);

            return extracted[entry.Key!];
        }, ct);

    private static ArchiveExtractResult ResolveCue(IArchiveEntry cueEntry, List<IArchiveEntry> entries, string extractDir, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        var cueExtracted = ExtractEntries([cueEntry], extractDir, progress, ct);
        string cuePath = cueExtracted[cueEntry.Key!];

        var referencedBins = ConversionSource.ParseBinsFromCue(cuePath);

        string cueDir = GetEntryDirectory(cueEntry.Key!);

        var binEntries = new List<IArchiveEntry>();

        foreach (var bin in referencedBins)
        {
            string binFileName = Path.GetFileName(bin);

            var match = entries.FirstOrDefault(e =>
                GetEntryDirectory(e.Key!) == cueDir &&
                string.Equals(Path.GetFileName(e.Key), binFileName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                binEntries.Add(match);
        }

        if (binEntries.Count == 0)
            throw new InvalidOperationException("CUE 파일이 참조하는 BIN 파일을 압축 안에서 찾을 수 없습니다.");

        var binExtracted = ExtractEntries(binEntries, extractDir, progress, ct);

        return new ArchiveExtractResult { ResolvedPath = binExtracted[binEntries[0].Key!] };
    }

    private static string GetEntryDirectory(string key)
    {
        string normalized = key.Replace('\\', '/');
        int lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[..lastSlash] : string.Empty;
    }

    private static Dictionary<string, string> ExtractEntries(List<IArchiveEntry> entries, string extractDir, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        long totalBytes = entries.Sum(e => e.Size);
        long writtenTotal = 0;

        var reporter = new ProgressReporter("원본 압축 해제 중...", string.Empty, totalBytes, progress);
        var report = reporter.CreateAction();

        var result = new Dictionary<string, string>();
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

            result[entry.Key!] = destPath;
        }

        reporter.ForceReport();

        return result;
    }
}