using CD.Core.Models;
using System.Text.RegularExpressions;

namespace CD.Core.Services;

public static class CueFileReader
{
    private static readonly Regex FileRegex = new(@"^FILE\s+""(.*?)""\s+(.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrackRegex = new(@"^\s*TRACK\s+(\d+)\s+(.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IndexRegex = new(@"^\s*INDEX\s+(\d+)\s+(\d+:\d+:\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CueFile Read(string file)
    {
        var cueFile = new CueFile { FilePath = file };

        ParseLines(File.ReadLines(file), cueFile);

        return cueFile;
    }

    public static CueFile Parse(string content)
    {
        var cueFile = new CueFile();
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);

        ParseLines(lines, cueFile);

        return cueFile;
    }

    private static void ParseLines(IEnumerable<string> lines, CueFile cueFile)
    {
        CueFileEntry? cueFileEntry = null;
        CueTrack? cueTrack = null;

        foreach (var line in lines)
        {
            var fileMatch = FileRegex.Match(line);
            var trackMatch = TrackRegex.Match(line);
            var indexMatch = IndexRegex.Match(line);

            if (fileMatch.Success)
            {
                cueFileEntry = new CueFileEntry
                {
                    FileName = fileMatch.Groups[1].Value,
                    FileType = fileMatch.Groups[2].Value.ToUpperInvariant(),
                    Tracks = []
                };
                cueFile.Entries.Add(cueFileEntry);
            }
            else if (trackMatch.Success && cueFileEntry != null)
            {
                cueTrack = new CueTrack
                {
                    Number = int.Parse(trackMatch.Groups[1].Value),
                    DataType = trackMatch.Groups[2].Value.ToUpperInvariant(),
                    Indexes = []
                };
                cueFileEntry.Tracks.Add(cueTrack);
            }
            else if (indexMatch.Success && cueTrack != null)
            {
                var pos = indexMatch.Groups[2].Value.Split(':');

                cueTrack.Indexes.Add(new CueIndex
                {
                    Number = int.Parse(indexMatch.Groups[1].Value),
                    Position = new MsfPosition
                    {
                        Minutes = int.Parse(pos[0]),
                        Seconds = int.Parse(pos[1]),
                        Frames = int.Parse(pos[2])
                    }
                });
            }
        }
    }
}