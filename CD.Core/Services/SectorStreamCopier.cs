namespace CD.Core.Services;

internal static class SectorStreamCopier
{
    public static async Task CopySectorsAsync(Stream source, Stream destination, int sectorCount, int sourceSectorSize, int outputSectorSize, Action? onSectorWritten, CancellationToken ct)
    {
        var buffer = new byte[outputSectorSize];
        var trailingSkip = sourceSectorSize - outputSectorSize;

        for (var i = 0; i < sectorCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            await ReadExactAsync(source, buffer, outputSectorSize, ct);
            await destination.WriteAsync(buffer, ct);

            if (trailingSkip > 0)
                await SkipAsync(source, trailingSkip, ct);

            onSectorWritten?.Invoke();
        }
    }

    public static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
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