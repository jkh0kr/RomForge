using System.IO.Compression;

namespace Patch.Core.Services;

public static class ZipPatchSource
{
    public static bool IsZipPath(string? path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public static string? FindSubDir(ZipArchive archive, string folderName)
    {
        string? best = null;
        int bestDepth = int.MaxValue;

        foreach (string dir in CollectDirs(archive))
        {
            if (dir.Length == 0)
                continue;

            int slash = dir.LastIndexOf('/');
            string name = slash < 0 ? dir : dir[(slash + 1)..];

            if (!string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase))
                continue;

            int depth = dir.Count(c => c == '/');

            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = dir + "/";
            }
        }

        return best;
    }

    public static string? FindPatchRoot(ZipArchive archive, params string[] anchorNames)
    {
        var allDirs = CollectDirs(archive);
        string? best = null;
        int bestDepth = int.MaxValue;

        foreach (string dir in allDirs)
        {
            bool hasAnchor = anchorNames.Any(a => allDirs.Contains(dir.Length == 0 ? a : $"{dir}/{a}"));

            if (!hasAnchor)
                continue;

            int depth = dir.Length == 0 ? 0 : dir.Count(c => c == '/') + 1;

            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = dir.Length == 0 ? "" : dir + "/";
            }
        }

        return best;
    }

    private static HashSet<string> CollectDirs(ZipArchive archive)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            string path = entry.FullName.Replace('\\', '/');
            int idx = path.LastIndexOf('/');
            string dir = idx < 0 ? "" : path[..idx];

            while (true)
            {
                dirs.Add(dir);

                if (dir.Length == 0)
                    break;

                int i = dir.LastIndexOf('/');
                dir = i < 0 ? "" : dir[..i];
            }
        }

        return dirs;
    }
}
