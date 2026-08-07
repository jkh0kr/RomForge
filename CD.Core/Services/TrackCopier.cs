using CD.Core.Models;
using Common;

namespace CD.Core.Services;

internal static class TrackCopier
{
    private const int OutputSectorSize = 2352;
    private const int ChunkSectors = 512;

    public static async Task<long> CopyTrackAsync(Stream src, Stream dst, DiscTrack track, long sectorsDone, long totalSectors, string label, string outputBaseName, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var skip = track.SourceSectorSize - OutputSectorSize;

        if (skip == 0)
        {
            var chunkBuffer = new byte[ChunkSectors * OutputSectorSize];
            var remaining = track.LengthSectors;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                var sectorsThisChunk = Math.Min(ChunkSectors, remaining);
                var bytesThisChunk = sectorsThisChunk * OutputSectorSize;

                await ReadExactAsync(src, chunkBuffer, bytesThisChunk, ct);
                await dst.WriteAsync(chunkBuffer.AsMemory(0, bytesThisChunk), ct);

                remaining -= sectorsThisChunk;
                sectorsDone += sectorsThisChunk;

                progress?.Report(new ProgressInfo(
                    (int)(totalSectors > 0 ? sectorsDone * 100 / totalSectors : 100),
                    label, outputBaseName, string.Empty, string.Empty));
            }
        }
        else
        {
            var buffer = new byte[OutputSectorSize];

            for (var i = 0; i < track.LengthSectors; i++)
            {
                ct.ThrowIfCancellationRequested();

                await ReadExactAsync(src, buffer, OutputSectorSize, ct);
                await dst.WriteAsync(buffer, ct);
                await SkipAsync(src, skip, ct);

                sectorsDone++;

                if (sectorsDone % 256 == 0 || sectorsDone == totalSectors)
                    progress?.Report(new ProgressInfo(
                        (int)(totalSectors > 0 ? sectorsDone * 100 / totalSectors : 100),
                        label, outputBaseName, string.Empty, string.Empty));
            }
        }

        return sectorsDone;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;

        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);

            if (n == 0)
                throw new EndOfStreamException("트랙 데이터를 읽는 도중 스트림이 예상보다 일찍 끝났습니다.");

            read += n;
        }
    }

    private static async Task SkipAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await ReadExactAsync(stream, buffer, count, ct);
    }
}