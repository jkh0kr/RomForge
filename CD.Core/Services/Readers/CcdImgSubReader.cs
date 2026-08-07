using CD.Core.Constants;
using CD.Core.Interfaces;
using CD.Core.Models;
using System.Text.RegularExpressions;

namespace CD.Core.Services.Readers;

public class CcdImgSubReader : IDiscImageReader
{
    private const int SectorSize = 2352;

    private static readonly Regex TrackHeaderRegex = new(@"^\[TRACK\s+\d+\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModeRegex = new(@"^MODE\s*=\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IndexRegex = new(@"^INDEX\s+(\d+)\s*=\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanRead(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".ccd", StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(filePath);
    }

    public DiscImage Read(string filePath)
    {
        var imgPath = ResolveSiblingPath(filePath, ".img");
        var lines = File.ReadAllLines(filePath);

        var rawTracks = ParseTracks(lines);

        if (rawTracks.Count == 0)
            throw new InvalidDataException($"CCD 파일에서 유효한 [TRACK] 섹션을 찾지 못했습니다: {filePath}");

        var imgLength = new FileInfo(imgPath).Length;

        if (imgLength % SectorSize != 0)
            throw new InvalidDataException($"IMG 파일 크기가 섹터 크기(2352)의 배수가 아닙니다: {imgPath}");

        var totalImgSectors = imgLength / SectorSize;
        var tracks = BuildDiscTracks(rawTracks, imgPath, totalImgSectors, filePath);

        return new DiscImage { Tracks = tracks };
    }

    private static string ResolveSiblingPath(string ccdPath, string extension)
    {
        var siblingPath = Path.ChangeExtension(ccdPath, extension);

        if (!File.Exists(siblingPath))
            throw new FileNotFoundException($"CCD와 짝을 이루는 {extension.ToUpperInvariant()} 파일을 찾을 수 없습니다: {siblingPath}");

        return siblingPath;
    }

    private static List<(int Mode, long? Index0, long Index1)> ParseTracks(string[] lines)
    {
        var tracks = new List<(int Mode, long? Index0, long Index1)>();
        var inTrack = false;
        var mode = -1;
        long? index0 = null;
        long? index1 = null;

        void FlushCurrent()
        {
            if (!inTrack)
                return;

            if (mode < 0 || index1 is null)
                throw new InvalidDataException("CCD 파일의 TRACK 섹션에 MODE 또는 INDEX 1이 누락되었습니다.");

            tracks.Add((mode, index0, index1.Value));
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (TrackHeaderRegex.IsMatch(line))
            {
                FlushCurrent();

                inTrack = true;
                mode = -1;
                index0 = null;
                index1 = null;

                continue;
            }

            if (!inTrack)
                continue;

            var modeMatch = ModeRegex.Match(line);

            if (modeMatch.Success)
            {
                mode = int.Parse(modeMatch.Groups[1].Value);
                continue;
            }

            var indexMatch = IndexRegex.Match(line);

            if (indexMatch.Success)
            {
                var indexNumber = int.Parse(indexMatch.Groups[1].Value);
                var indexValue = long.Parse(indexMatch.Groups[2].Value);

                if (indexNumber == 0)
                    index0 = indexValue;
                else if (indexNumber == 1)
                    index1 = indexValue;
            }
        }

        FlushCurrent();

        return tracks;
    }

    private static List<DiscTrack> BuildDiscTracks(List<(int Mode, long? Index0, long Index1)> rawTracks, string imgPath, long totalImgSectors, string ccdFilePath)
    {
        var tracks = new List<DiscTrack>();

        for (var i = 0; i < rawTracks.Count; i++)
        {
            var (mode, index0, index1) = rawTracks[i];

            var trackStart = index0 ?? index1;
            var pregapSectors = index0.HasValue ? index1 - index0.Value : 0;

            var nextStart = i + 1 < rawTracks.Count
                ? (rawTracks[i + 1].Index0 ?? rawTracks[i + 1].Index1)
                : totalImgSectors;

            var lengthSectors = nextStart - index1;

            if (trackStart < 0 || nextStart > totalImgSectors || lengthSectors <= 0)
                throw new InvalidDataException(
                    $"트랙 {i + 1}의 섹터 범위가 IMG 파일 크기를 벗어나거나 비정상입니다 " +
                    $"(start={trackStart}, length={lengthSectors}, imgSectors={totalImgSectors}): {ccdFilePath}");

            var streamOffset = trackStart * SectorSize;
            var streamLength = (nextStart - trackStart) * SectorSize;

            tracks.Add(new DiscTrack
            {
                Number = i + 1,
                DataType = MapTrackMode(mode),
                PregapSectors = (int)pregapSectors,
                LengthSectors = (int)lengthSectors,
                SourceSectorSize = SectorSize,
                SubchannelSize = 0,
                OpenSectorStream = () => OpenTrackStream(imgPath, streamOffset, streamLength)
            });
        }

        return tracks;
    }

    private static SubStream OpenTrackStream(string imgPath, long startOffset, long length)
    {
        var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(startOffset, SeekOrigin.Begin);

        return new SubStream(fs, length);
    }

    private static string MapTrackMode(int mode) => mode switch
    {
        0 => CueFormatStrings.Audio,
        1 => CueFormatStrings.Mode1_2352,
        2 => CueFormatStrings.Mode2_2352,
        _ => throw new InvalidDataException($"알 수 없는 CCD 트랙 MODE 값입니다: {mode}")
    };
}