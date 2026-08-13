using LibHac.Util;
using System.Security.Cryptography;

namespace Patch.Core.Services;

public static class NsoTool
{
    private const int HeaderSize = 0x100;

    private record struct SegInfo(int FileOffField, int MemOffField, int SizeField, int FileSizeField, int CompBit, int HashBit, int HashOffset);

    private static readonly SegInfo[] Segs =
    [
        new(0x10, 0x14, 0x18, 0x60, 0, 3, 0xA0),
        new(0x20, 0x24, 0x28, 0x64, 1, 4, 0xC0),
        new(0x30, 0x34, 0x38, 0x68, 2, 5, 0xE0),
    ];

    public static bool IsNso(byte[] data) =>
        data.Length >= 4 && data[0] == 'N' && data[1] == 'S' && data[2] == 'O' && data[3] == '0';

    public static string GetBuildIdHex(byte[] nso) => Convert.ToHexString(nso.AsSpan(0x40, 32));

    public static bool IsCompressed(byte[] nso)
    {
        uint flags = BitConverter.ToUInt32(nso, 0x0C);
        return Segs.Any(s => (flags & (1u << s.CompBit)) != 0);
    }

    public static byte[] DecompressToPlain(byte[] nso)
    {
        if (!IsNso(nso))
            throw new InvalidDataException("유효한 NSO 파일이 아닙니다.");

        uint flags = BitConverter.ToUInt32(nso, 0x0C);
        var segData = new byte[3][];

        for (int i = 0; i < 3; i++)
        {
            var s = Segs[i];
            int fileOff = BitConverter.ToInt32(nso, s.FileOffField);
            int memSize = BitConverter.ToInt32(nso, s.SizeField);
            bool compressed = (flags & (1u << s.CompBit)) != 0;
            int fileSize = compressed ? BitConverter.ToInt32(nso, s.FileSizeField) : memSize;

            byte[] raw = new byte[fileSize];
            Array.Copy(nso, fileOff, raw, 0, fileSize);

            segData[i] = compressed ? Lz4.Decompress(raw, memSize) : raw;
        }

        byte[] outNso = new byte[HeaderSize + segData.Sum(s => s.Length)];
        Array.Copy(nso, 0, outNso, 0, HeaderSize);

        uint newFlags = flags;
        int cursor = HeaderSize;

        for (int i = 0; i < 3; i++)
        {
            var s = Segs[i];
            Array.Copy(segData[i], 0, outNso, cursor, segData[i].Length);

            BitConverter.GetBytes(cursor).CopyTo(outNso, s.FileOffField);
            BitConverter.GetBytes(segData[i].Length).CopyTo(outNso, s.FileSizeField);

            newFlags &= ~(1u << s.CompBit);

            if ((flags & (1u << s.HashBit)) != 0)
                SHA256.HashData(segData[i]).CopyTo(outNso, s.HashOffset);

            cursor += segData[i].Length;
        }

        BitConverter.GetBytes(newFlags).CopyTo(outNso, 0x0C);
        return outNso;
    }

    public static void UpdateHashes(byte[] nsoData)
    {
        int textFileOff = BitConverter.ToInt32(nsoData, 0x10);
        int textSize = BitConverter.ToInt32(nsoData, 0x60);

        int rodataOff = BitConverter.ToInt32(nsoData, 0x20);
        int rodataSize = BitConverter.ToInt32(nsoData, 0x64);

        int dataOff = BitConverter.ToInt32(nsoData, 0x30);
        int dataSize = BitConverter.ToInt32(nsoData, 0x68);

        using var sha256 = SHA256.Create();

        if (textSize > 0)
        {
            byte[] textHash = sha256.ComputeHash(nsoData, textFileOff, textSize);
            Buffer.BlockCopy(textHash, 0, nsoData, 0xA0, 32);
        }

        if (rodataSize > 0)
        {
            byte[] rodataHash = sha256.ComputeHash(nsoData, rodataOff, rodataSize);
            Buffer.BlockCopy(rodataHash, 0, nsoData, 0xC0, 32);
        }

        if (dataSize > 0)
        {
            byte[] dataHash = sha256.ComputeHash(nsoData, dataOff, dataSize);
            Buffer.BlockCopy(dataHash, 0, nsoData, 0xE0, 32);
        }
    }
}