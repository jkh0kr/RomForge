using System.IO.Compression;

namespace Patch.Core.Services;

public enum PatchFileKind { Overwrite, BinaryPatch }

public record PatchFileEntry(PatchFileRef File, string RelativeDir, string BaseName, PatchFileKind Kind);

public class PatchFileIndex
{
    public static readonly string[] PatchExtensions =
        [".xdelta", ".xdelta3", ".ips", ".ups", ".bps", ".ppf", ".aps"];

    public List<PatchFileEntry> Entries { get; } = [];

    public bool HasAnyFile => Entries.Count > 0;

    public static PatchFileIndex Build(string patchDir)
    {
        var index = new PatchFileIndex();

        if (!Directory.Exists(patchDir))
            return index;

        foreach (string file in Directory.EnumerateFiles(patchDir, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            bool isPatch = PatchExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
            string rel = Path.GetRelativePath(patchDir, file);
            string relDir = Path.GetDirectoryName(rel) ?? string.Empty;
            string baseName = isPatch ? Path.GetFileNameWithoutExtension(rel) : Path.GetFileName(rel);

            index.Entries.Add(new PatchFileEntry(PatchFileRef.FromDisk(file), relDir, baseName, isPatch ? PatchFileKind.BinaryPatch : PatchFileKind.Overwrite));
        }

        return index;
    }

    public static PatchFileIndex Build(ZipArchive archive, string prefix)
    {
        var index = new PatchFileIndex();
        string normalizedPrefix = prefix.Length == 0 ? "" : prefix.TrimEnd('/') + "/";

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // 디렉터리 전용 엔트리 스킵

            string key = entry.FullName.Replace('\\', '/');

            if (!key.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rel = key[normalizedPrefix.Length..];

            if (rel.Length == 0)
                continue;

            string ext = Path.GetExtension(rel);
            bool isPatch = PatchExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
            int lastSlash = rel.LastIndexOf('/');
            string relDir = lastSlash < 0 ? "" : rel[..lastSlash];
            string fileName = lastSlash < 0 ? rel : rel[(lastSlash + 1)..];
            string baseName = isPatch ? Path.GetFileNameWithoutExtension(fileName) : fileName;

            var capturedEntry = entry; // 클로저 캡처(foreach 변수 캡처 문제 방지용 명시적 복사)
            var fileRef = PatchFileRef.FromZip(() => capturedEntry.Open(), capturedEntry.Length);

            index.Entries.Add(new PatchFileEntry(fileRef, relDir, baseName, isPatch ? PatchFileKind.BinaryPatch : PatchFileKind.Overwrite));
        }

        return index;
    }
}