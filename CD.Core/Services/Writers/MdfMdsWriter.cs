using CD.Core.Constants;
using CD.Core.Models;
using Common;

namespace CD.Core.Services.Writers;

public static class MdfMdsWriter
{
    private const int OutputSectorSize = 2352;
    private const string Signature = "MEDIA DESCRIPTOR";
    private const int HeaderSize = 0x58;
    private const int SessionBlockSize = 0x18;
    private const int TrackBlockSize = 0x50;
    private const int IndexBlockSize = 8;

    private const byte TrackModeAudio = 0xA9;
    private const byte TrackModeMode1 = 0xAA;
    private const byte TrackModeMode2 = 0xAC;

    private const byte LeadInPointA0 = 0xA0;
    private const byte LeadInPointA1 = 0xA1;
    private const byte LeadInPointA2 = 0xA2;

    public static async Task<string> WriteAsync(DiscImage image, string outputDir, string outputBaseName, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        var mdfPath = Utils.GetUniqueFilePath(Path.Combine(outputDir, $"{outputBaseName}.mdf"));

        outputBaseName = Path.GetFileNameWithoutExtension(mdfPath);

        var mdsPath = Path.Combine(outputDir, $"{outputBaseName}.mds");

        var totalSectors = image.Tracks.Sum(t => (long)t.LengthSectors);
        long sectorsDone = 0;

        var trackOffsets = new List<long>();
        long currentOffset = 0;

        await using (var outStream = new FileStream(mdfPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            foreach (var track in image.Tracks)
            {
                ct.ThrowIfCancellationRequested();

                trackOffsets.Add(currentOffset);

                await using var src = track.OpenSectorStream();

                sectorsDone = await TrackCopier.CopyTrackAsync(src, outStream, track, sectorsDone, totalSectors, "MDF/MDS 변환 중", outputBaseName, progress, ct);

                currentOffset += (long)track.LengthSectors * OutputSectorSize;
            }
        }

        WriteMds(mdsPath, image, trackOffsets);

        return mdsPath;
    }

    private static void WriteMds(string mdsPath, DiscImage image, List<long> trackOffsets)
    {
        var trackCount = image.Tracks.Count;
        var totalBlocks = trackCount + 3;
        var trackBlocksOffset = HeaderSize + SessionBlockSize;
        var indexBlocksOffset = trackBlocksOffset + totalBlocks * TrackBlockSize;
        var fileSize = indexBlocksOffset + trackCount * IndexBlockSize;

        var bytes = new byte[fileSize];
        var sig = System.Text.Encoding.ASCII.GetBytes(Signature);

        Array.Copy(sig, bytes, sig.Length);

        bytes[HeaderSize + 0x0A] = (byte)totalBlocks;
        BitConverter.GetBytes((uint)trackBlocksOffset).CopyTo(bytes, HeaderSize + 0x14);

        bytes[trackBlocksOffset + 0 * TrackBlockSize + 0x04] = LeadInPointA0;
        bytes[trackBlocksOffset + 1 * TrackBlockSize + 0x04] = LeadInPointA1;
        bytes[trackBlocksOffset + 2 * TrackBlockSize + 0x04] = LeadInPointA2;

        var trackArrayOffset = trackBlocksOffset + 3 * TrackBlockSize;
        var indexPos = indexBlocksOffset;

        for (var i = 0; i < trackCount; i++)
        {
            var track = image.Tracks[i];
            var blockPos = trackArrayOffset + i * TrackBlockSize;

            bytes[blockPos + 0x00] = MapDataTypeToMode(track.DataType);
            bytes[blockPos + 0x01] = 0;
            bytes[blockPos + 0x04] = (byte)track.Number;

            BitConverter.GetBytes((uint)indexPos).CopyTo(bytes, blockPos + 0x0C);
            BitConverter.GetBytes((ushort)OutputSectorSize).CopyTo(bytes, blockPos + 0x10);
            BitConverter.GetBytes((long)trackOffsets[i]).CopyTo(bytes, blockPos + 0x28);

            BitConverter.GetBytes((uint)track.PregapSectors).CopyTo(bytes, indexPos + 0);
            BitConverter.GetBytes((uint)track.LengthSectors).CopyTo(bytes, indexPos + 4);

            indexPos += IndexBlockSize;
        }

        File.WriteAllBytes(mdsPath, bytes);
    }

    private static byte MapDataTypeToMode(string dataType) => dataType switch
    {
        CueFormatStrings.Audio => TrackModeAudio,
        CueFormatStrings.Mode1_2352 => TrackModeMode1,
        _ => TrackModeMode2
    };
}