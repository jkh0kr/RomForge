using CD.Core.Models;
using Common;

namespace CD.Core.Services.Writers;

public static class IsoWriter
{
    private const int OutputSectorSize = 2352;

    public static async Task<string> WriteAsync(DiscImage image, string outputDir, string outputBaseName, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (image.Tracks.Count != 1)
            throw new NotSupportedException($"ISO 변환은 단일 트랙 이미지에서만 가능합니다(현재 트랙 수: {image.Tracks.Count}).");

        Directory.CreateDirectory(outputDir);

        var isoPath = Utils.GetUniqueFilePath(Path.Combine(outputDir, $"{outputBaseName}.iso"));
        var track = image.Tracks[0];
        var totalSectors = track.TotalSectors;
        long sectorsDone = 0;

        await using (var outStream = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        await using (var src = track.OpenSectorStream())
        {
            await SectorStreamCopier.CopySectorsAsync(
                src,
                outStream,
                track.TotalSectors,
                track.SourceSectorSize,
                OutputSectorSize,
                count =>
                {
                    sectorsDone += count;

                    progress?.Report(new ProgressInfo(
                        (int)(totalSectors > 0 ? sectorsDone * 100 / totalSectors : 100),
                        "ISO 변환 중",
                        outputBaseName,
                        string.Empty,
                        string.Empty));
                },
                ct);
        }

        return isoPath;
    }
}