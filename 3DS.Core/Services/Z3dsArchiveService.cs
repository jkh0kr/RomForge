using _3DS.Core.Crypto;
using _3DS.Core.IO;
using _3DS.Core.Models;
using Common;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace _3DS.Core.Services;

public class Z3dsArchiveService
{
    private static readonly byte[] MagicZ3DS = "Z3DS"u8.ToArray();
    private static readonly byte[] MagicNcsd = "NCSD"u8.ToArray();
    private const string CompressExtension = ".zcci";
    private const string DecompressExtension = ".cci";
    private const uint SeekableMagicNumber = 0x184D2A5E;
    private const uint SeekTableFooterMagic = 0x8F92EAB1;
    private const int FrameSize = 32 * 1024 * 1024;
    private const byte FormatVersion = 0x01;
    private const byte MetaVersion = 0x01;
    private const byte MetaTypeEnd = 0x00;
    private const byte MetaTypeBinary = 0x01;
    private const int MaxMetaDataLength = 0xFFFF;

    public static async Task CompressAsync(string inputPath, int compressionLevel = 18, IProgress<ProgressInfo>? progress = null, Action<string, LogLevel>? log = null, CancellationToken ct = default)
    {
        string? outputPath = null;
        bool isCompleted = false;

        try
        {
            var fileStream = File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Stream inputStream = fileStream;

            byte[] headerBuffer = new byte[0x200];

            fileStream.Position = 0x4000;

            await fileStream.ReadExactlyAsync(headerBuffer, 0, 0x200, ct);

            var header = NcchHeader.Parse(headerBuffer, 0);

            if (!header.NoCrypto)
            {
                log?.Invoke("암호화된 롬 감지, 복호화 파이프라인 구동...", LogLevel.Info);

                var keyStore = new KeyStore();

                fileStream.Position = 0;

                var ncsdHeader = new SubStream(fileStream, 0, 0x4000);
                var ncchDecrypted = new NcchDecryptionStream(fileStream, 0x4000, keyStore);

                inputStream = new ConcatStream(ncsdHeader, ncchDecrypted);
            }
            else
            {
                fileStream.Position = 0;
            }

            await using (inputStream)
            {
                outputPath = Utils.GetUniqueFilePath(Path.ChangeExtension(inputPath, CompressExtension));

                using var outputStream = File.Open(outputPath, FileMode.Create, FileAccess.Write);

                log?.Invoke($"{Path.GetFileName(inputPath)} 압축 시작", LogLevel.Highlight);
                await CompressInternalAsync(inputStream, outputStream, fileStream.Length, compressionLevel, progress, ct);

                long originalSize = new FileInfo(inputPath).Length;
                long compressedSize = new FileInfo(outputPath).Length;

                log?.Invoke($"압축률: {Utils.FormatFileSize(originalSize)} → {Utils.FormatFileSize(compressedSize)} ({compressedSize * 100.0 / originalSize:F1}%)", LogLevel.Highlight);
                log?.Invoke($"압축 완료: {outputPath}", LogLevel.Ok);
            }

            isCompleted = true;
        }
        finally
        {
            if (!isCompleted && !string.IsNullOrEmpty(outputPath) && File.Exists(outputPath))
                try { File.Delete(outputPath); } catch { }
        }
    }

    public static async Task CompressFromCiaAsync(string inputPath, int compressionLevel = 18, IProgress<ProgressInfo>? progress = null, Action<string, LogLevel>? log = null, CancellationToken ct = default)
    {
        string? outputPath = null;
        bool isCompleted = false;

        try
        {
            var keyStore = new KeyStore();
            var unpacker = new CiaReader(keyStore);
            await using var ctx = await unpacker.OpenAsync(inputPath, log, ct);
            uint titleType = (uint)(ctx.Ticket.TitleId >> 32);

            if (titleType != 0x00040000)
            {
                string typeDescription = titleType switch
                {
                    0x0004000E => "업데이트",
                    0x0004008C => "DLC",
                    _ => $"미지원 콘텐츠 타입 (Type ID: 0x{titleType:X8})"
                };

                throw new NotSupportedException($"{typeDescription} 파일은 CCI 복원이 불가능합니다. (본편만 가능)");
            }

            outputPath = Utils.GetUniqueFilePath(Path.ChangeExtension(inputPath, CompressExtension));

            using var outputStream = File.Open(outputPath, FileMode.Create, FileAccess.Write);
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 64 * 1024 * 1024, resumeWriterThreshold: 32 * 1024 * 1024));
            var buildTask = Task.Run(async () =>
            {
                try
                {
                    await NcsdBuilder.BuildAsync(ctx, new PipeWriterStream(pipe.Writer), null, ct);
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex) { await pipe.Writer.CompleteAsync(ex); }
            }, ct);

            long uncompressedSize = NcsdBuilder.CalculateOutputSize(ctx);

            log?.Invoke($"{Path.GetFileName(inputPath)} 압축 시작", LogLevel.Highlight);

            await CompressInternalAsync(new PipeReaderStream(pipe.Reader), outputStream, uncompressedSize, compressionLevel, progress, ct);

            await buildTask;

            long originalSize = new FileInfo(inputPath).Length;
            long compressedSize = new FileInfo(outputPath).Length;

            log?.Invoke($"압축률: {Utils.FormatFileSize(originalSize)} → {Utils.FormatFileSize(compressedSize)} ({compressedSize * 100.0 / originalSize:F1}%)", LogLevel.Highlight);
            log?.Invoke($"압축 완료: {outputPath}", LogLevel.Ok);

            isCompleted = true;
        }
        finally
        {
            if (!isCompleted && !string.IsNullOrEmpty(outputPath) && File.Exists(outputPath))
                try { File.Delete(outputPath); } catch { }
        }
    }

    private static async Task CompressInternalAsync(Stream input, Stream output, long uncompressedSize, int compressionLevel, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        long readBytes = 0;
        long writtenBytes = 0;
        byte[] underlyingMagic = MagicNcsd;

        if (input.CanSeek && input.Length > 0x104)
        {
            input.Position = 0x100;

            byte[] magicBuf = new byte[4];

            await input.ReadExactlyAsync(magicBuf, 0, 4, ct);

            underlyingMagic = magicBuf;
            input.Position = 0;
        }

        byte[] metadata = BuildMetadata(FrameSize);
        int metadataAligned = AlignUp(metadata.Length, 16);
        byte[] metadataPadded = new byte[metadataAligned];

        metadata.CopyTo(metadataPadded, 0);

        long headerOffset = output.Position;

        await output.WriteAsync(new byte[0x20 + metadataAligned], ct);

        long bodyStartOffset = output.Position;
        int blockCount = (int)((uncompressedSize + FrameSize - 1) / FrameSize);
        var tasks = new Task<(byte[] data, int size, int index)>[blockCount];
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

        for (int i = 0; i < blockCount; i++)
        {
            int size = (int)Math.Min(FrameSize, uncompressedSize - (long)i * FrameSize);
            byte[] buf = new byte[size];

            await input.ReadExactlyAsync(buf, 0, size, ct);

            Interlocked.Add(ref readBytes, size);
            progress?.Report(new ProgressInfo { Percent = (int)((double)Interlocked.Read(ref readBytes) / uncompressedSize * 50.0) });

            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var compressor = new Compressor(compressionLevel);

                    compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, 25);

                    int maxBound = Compressor.GetCompressBound(size);
                    byte[] compBuf = new byte[maxBound];
                    int compSize = compressor.Wrap(buf, compBuf);
                    byte[] result = new byte[compSize];

                    Array.Copy(compBuf, result, compSize);

                    return (result, size, i);
                }
                finally { semaphore.Release(); }
            }, ct);
        }

        var seekEntries = new SeekEntry[blockCount];
        var completed = new bool[blockCount];

        for (int i = 0; i < blockCount; i++)
        {
            var (data, size, index) = await tasks[i];
            await output.WriteAsync(data, ct);

            seekEntries[i] = new SeekEntry { CompressedSize = (uint)data.Length, DecompressedSize = (uint)size };

            Interlocked.Add(ref writtenBytes, size);
            progress?.Report(new ProgressInfo { Percent = 50 + (int)((double)Interlocked.Read(ref writtenBytes) / uncompressedSize * 50.0) });
        }

        WriteSeekTable(output, [.. seekEntries]);

        long endOffset = output.Position;
        output.Position = headerOffset;

        WriteZ3dsHeader(output, underlyingMagic, (uint)metadataAligned, endOffset - bodyStartOffset, uncompressedSize);
        await output.WriteAsync(metadataPadded, ct);

        output.Position = endOffset;

        progress?.Report(new ProgressInfo { Percent = 100 });
    }

    public static async Task DecompressAsync(string inputPath, IProgress<ProgressInfo>? progress = null, Action<string, LogLevel>? log = null, CancellationToken ct = default)
    {
        string? outputPath = null;
        bool isCompleted = false;

        try
        {
            using var inputStream = File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = ParseZ3dsHeader(inputStream);

            outputPath = Utils.GetUniqueFilePath(Path.ChangeExtension(inputPath, DecompressExtension));

            using var outputStream = File.Open(outputPath, FileMode.Create, FileAccess.Write);
            long totalSize = header.UncompressedSize;
            long processed = 0;

            log?.Invoke($"{Path.GetFileName(inputPath)} 해제 시작", LogLevel.Highlight);

            long compressedDataOffset = header.HeaderSize + header.MetadataSize;
            long compressedDataLength = header.CompressedSize;

            await DecompressBlocksAsync(inputStream, outputStream, compressedDataOffset, compressedDataLength,
                bytesProcessed =>
                {
                    processed += bytesProcessed;
                    progress?.Report(new ProgressInfo { Percent = (int)((double)processed / totalSize * 100) });
                }, ct);

            isCompleted = true;
            log?.Invoke($"해제 완료: {outputPath}", LogLevel.Ok);
        }
        finally
        {
            if (!isCompleted && !string.IsNullOrEmpty(outputPath) && File.Exists(outputPath))
                try { File.Delete(outputPath); } catch { }
        }
    }

    private static async Task DecompressBlocksAsync(Stream input, Stream output, long dataOffset, long compressedDataLength, Action<long>? onProgress, CancellationToken ct)
    {
        var seekEntries = ParseSeekTable(input, dataOffset, compressedDataLength);
        long seekTableSize = 4 + 4 + (seekEntries.Count * 8) + 9;
        long totalBlockBytes = seekEntries.Sum(e => (long)e.CompressedSize);
        long expectedDataLength = totalBlockBytes + seekTableSize;

        if (expectedDataLength != compressedDataLength)
            throw new InvalidDataException($"Seek table 경계 불일치: 합산={totalBlockBytes}, 요청={compressedDataLength}");

        input.Position = dataOffset;

        using var decompressor = new Decompressor();
        var pool = ArrayPool<byte>.Shared;

        foreach (var entry in seekEntries)
        {
            ct.ThrowIfCancellationRequested();

            byte[] compressedBuf = pool.Rent((int)entry.CompressedSize);
            byte[] decompressedBuf = pool.Rent((int)entry.DecompressedSize);

            try
            {
                await input.ReadExactlyAsync(compressedBuf.AsMemory(0, (int)entry.CompressedSize), ct);

                int unwrapSize = decompressor.Unwrap(compressedBuf.AsSpan(0, (int)entry.CompressedSize), decompressedBuf.AsSpan(0, (int)entry.DecompressedSize));

                await output.WriteAsync(decompressedBuf.AsMemory(0, unwrapSize), ct);

                onProgress?.Invoke(unwrapSize);
            }
            finally
            {
                pool.Return(compressedBuf);
                pool.Return(decompressedBuf);
            }
        }
    }

    private static void WriteSeekTable(Stream output, List<SeekEntry> entries)
    {
        Span<byte> buf = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(buf, SeekableMagicNumber);
        output.Write(buf);

        uint tableSize = (uint)(entries.Count * 8 + 9);

        BinaryPrimitives.WriteUInt32LittleEndian(buf, tableSize);
        output.Write(buf);

        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf, entry.CompressedSize);
            output.Write(buf);
            BinaryPrimitives.WriteUInt32LittleEndian(buf, entry.DecompressedSize);
            output.Write(buf);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)entries.Count);
        output.Write(buf);
        output.WriteByte(0x00);
        BinaryPrimitives.WriteUInt32LittleEndian(buf, SeekTableFooterMagic);
        output.Write(buf);
    }

    public static List<SeekEntry> ParseSeekTable(Stream input, long dataStart, long compressedDataLength)
    {
        long seekTableEnd = dataStart + compressedDataLength;

        input.Position = seekTableEnd - 9;

        Span<byte> footer = stackalloc byte[9];

        input.ReadExactly(footer);

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(footer[0..4]);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(footer[5..9]);

        if (magic != SeekTableFooterMagic)
            throw new InvalidDataException("Seekable zstd seek table magic mismatch");

        long tableBodyStart = seekTableEnd - 9 - (entryCount * 8);

        input.Position = tableBodyStart;

        var entries = new List<SeekEntry>((int)entryCount);
        byte[] entryPool = ArrayPool<byte>.Shared.Rent((int)entryCount * 8);

        try
        {
            input.ReadExactly(entryPool, 0, (int)entryCount * 8);

            for (int i = 0; i < entryCount; i++)
            {
                int offset = i * 8;

                entries.Add(new SeekEntry
                {
                    CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(entryPool.AsSpan(offset, 4)),
                    DecompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(entryPool.AsSpan(offset + 4, 4))
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(entryPool);
        }

        return entries;
    }

    private static void WriteZ3dsHeader(Stream output, byte[] underlyingMagic, uint metadataSize, long compressedSize, long uncompressedSize)
    {
        Span<byte> header = stackalloc byte[0x20];

        header.Clear();
        MagicZ3DS.CopyTo(header[0x00..]);
        underlyingMagic.AsSpan(0, 4).CopyTo(header[0x04..]);

        header[0x08] = FormatVersion;
        header[0x09] = 0x00;

        BinaryPrimitives.WriteUInt16LittleEndian(header[0x0A..], 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0C..], metadataSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[0x10..], compressedSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[0x18..], uncompressedSize);
        output.Write(header);
    }

    public static Z3dsHeader ParseZ3dsHeader(Stream input)
    {
        Span<byte> buf = stackalloc byte[0x20];

        input.ReadExactly(buf);

        if (!buf[0x00..0x04].SequenceEqual(MagicZ3DS))
            throw new InvalidDataException("Not a Z3DS file (magic mismatch)");

        return new Z3dsHeader
        {
            UnderlyingMagic = buf[0x04..0x08].ToArray(),
            Version = buf[0x08],
            HeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(buf[0x0A..]),
            MetadataSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[0x0C..]),
            CompressedSize = BinaryPrimitives.ReadInt64LittleEndian(buf[0x10..]),
            UncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(buf[0x18..])
        };
    }

    private static byte[] BuildMetadata(int frameSize)
    {
        using var ms = new MemoryStream();

        ms.WriteByte(MetaVersion);
        WriteMetaItem(ms, "compressor", Encoding.UTF8.GetBytes("RomZip"));
        WriteMetaItem(ms, "date", Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o")));

        byte[] frameSizeBytes = BitConverter.GetBytes(frameSize);

        WriteMetaItem(ms, "maxframesize", frameSizeBytes);
        ms.Write([MetaTypeEnd, 0x00, 0x00, 0x00]);

        return ms.ToArray();
    }

    private static void WriteMetaItem(Stream output, string name, byte[] data)
    {
        if (data.Length > MaxMetaDataLength)
            throw new ArgumentOutOfRangeException(nameof(data), $"metadata 값 크기가 최대치를 초과합니다: {data.Length} bytes");

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);

        output.WriteByte(MetaTypeBinary);
        output.WriteByte((byte)nameBytes.Length);
        output.WriteByte((byte)(data.Length & 0xFF));
        output.WriteByte((byte)(data.Length >> 8));
        output.Write(nameBytes);
        output.Write(data);
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}