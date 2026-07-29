namespace Patch.Core.Services;

public enum PatchFileKind { Overwrite, BinaryPatch }

public record PatchFileEntry(string FullPath, string RelativeDir, string BaseName, PatchFileKind Kind);

public class PatchFileIndex
{
    private static readonly string[] PatchExtensions = [".xdelta", ".xdelta3", ".ips", ".ups", ".bps", ".ppf", ".aps"];

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

            index.Entries.Add(new PatchFileEntry(file, relDir, baseName, isPatch ? PatchFileKind.BinaryPatch : PatchFileKind.Overwrite));
        }

        return index;
    }
}