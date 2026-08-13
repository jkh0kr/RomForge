// https://github.com/Ryujinx/Ryujinx/blob/0254a84f90ea03037be15b8fd1f9e0a4be5577e9/Ryujinx.HLE/Loaders/Compression/Lz4.cs

using System;
using System.Collections.Generic;

namespace LibHac.Util;

public static class Lz4
{
    private const int MinMatch = 4;
    private const int LastLiterals = 5;
    private const int HashLog = 16;
    private const int HashSize = 1 << HashLog;

    public static byte[] Decompress(byte[] cmp, int decLength)
    {
        byte[] dec = new byte[decLength];

        int cmpPos = 0;
        int decPos = 0;

        int GetLength(int length)
        {
            byte sum;

            if (length == 0xf)
            {
                do
                {
                    length += sum = cmp[cmpPos++];
                } while (sum == 0xff);
            }

            return length;
        }

        do
        {
            byte token = cmp[cmpPos++];

            int encCount = (token >> 0) & 0xf;
            int litCount = (token >> 4) & 0xf;

            //Copy literal chunk
            litCount = GetLength(litCount);

            Buffer.BlockCopy(cmp, cmpPos, dec, decPos, litCount);

            cmpPos += litCount;
            decPos += litCount;

            if (cmpPos >= cmp.Length)
            {
                break;
            }

            //Copy compressed chunk
            int back = cmp[cmpPos++] << 0 |
                       cmp[cmpPos++] << 8;

            encCount = GetLength(encCount) + 4;

            int encPos = decPos - back;

            if (encCount <= back)
            {
                Buffer.BlockCopy(dec, encPos, dec, decPos, encCount);

                decPos += encCount;
            }
            else
            {
                while (encCount-- > 0)
                {
                    dec[decPos++] = dec[encPos++];
                }
            }
        } while (cmpPos < cmp.Length &&
                 decPos < dec.Length);

        return dec;
    }

    public static void Decompress(ReadOnlySpan<byte> cmp, Span<byte> dec)
    {
        int cmpPos = 0;
        int decPos = 0;

        // ReSharper disable once VariableHidesOuterVariable
        int GetLength(int length, ReadOnlySpan<byte> cmp)
        {
            byte sum;

            if (length == 0xf)
            {
                do
                {
                    length += sum = cmp[cmpPos++];
                } while (sum == 0xff);
            }

            return length;
        }

        do
        {
            byte token = cmp[cmpPos++];

            int encCount = (token >> 0) & 0xf;
            int litCount = (token >> 4) & 0xf;

            //Copy literal chunk
            litCount = GetLength(litCount, cmp);

            cmp.Slice(cmpPos, litCount).CopyTo(dec.Slice(decPos));

            cmpPos += litCount;
            decPos += litCount;

            if (cmpPos >= cmp.Length)
            {
                break;
            }

            //Copy compressed chunk
            int back = cmp[cmpPos++] << 0 |
                       cmp[cmpPos++] << 8;

            encCount = GetLength(encCount, cmp) + 4;

            int encPos = decPos - back;

            if (encCount <= back)
            {
                dec.Slice(encPos, encCount).CopyTo(dec.Slice(decPos));

                decPos += encCount;
            }
            else
            {
                while (encCount-- > 0)
                {
                    dec[decPos++] = dec[encPos++];
                }
            }
        } while (cmpPos < cmp.Length &&
                 decPos < dec.Length);
    }

    public static byte[] Compress(byte[] src)
    {
        int n = src.Length;
        var output = new List<byte>(Math.Max(64, n / 2));

        if (n == 0)
            return [];

        int[] table = new int[HashSize];
        Array.Fill(table, -1);

        int Hash(int pos)
        {
            uint v = (uint)(src[pos] | src[pos + 1] << 8 | src[pos + 2] << 16 | src[pos + 3] << 24);
            return (int)((v * 2654435761u) >> (32 - HashLog));
        }

        void WriteLength(int length)
        {
            while (length >= 255)
            {
                output.Add(255);
                length -= 255;
            }
            output.Add((byte)length);
        }

        int literalStart = 0;
        int i = 0;
        int searchLimit = n - MinMatch - LastLiterals;

        while (i < n)
        {
            int matchPos = -1, matchLen = 0;

            if (i <= searchLimit)
            {
                int h = Hash(i);
                int cand = table[h];
                table[h] = i;

                if (cand >= 0 && i - cand <= 65535 &&
                    src[cand] == src[i] && src[cand + 1] == src[i + 1] &&
                    src[cand + 2] == src[i + 2] && src[cand + 3] == src[i + 3])
                {
                    int maxExtend = (n - LastLiterals) - i;
                    int len = 4;

                    while (len < maxExtend && src[cand + len] == src[i + len])
                        len++;

                    matchPos = cand;
                    matchLen = len;
                }
            }

            if (matchPos >= 0)
            {
                int litLen = i - literalStart;
                int matchLenEnc = matchLen - MinMatch;
                int back = i - matchPos;

                byte token = (byte)(((litLen < 15 ? litLen : 15) << 4) | (matchLenEnc < 15 ? matchLenEnc : 15));
                output.Add(token);

                if (litLen >= 15)
                    WriteLength(litLen - 15);

                for (int k = 0; k < litLen; k++)
                    output.Add(src[literalStart + k]);

                output.Add((byte)(back & 0xFF));
                output.Add((byte)((back >> 8) & 0xFF));

                if (matchLenEnc >= 15)
                    WriteLength(matchLenEnc - 15);

                i += matchLen;
                literalStart = i;
            }
            else
            {
                i++;
            }
        }

        int tailLen = n - literalStart;
        byte tailToken = (byte)((tailLen < 15 ? tailLen : 15) << 4);
        output.Add(tailToken);

        if (tailLen >= 15)
            WriteLength(tailLen - 15);

        for (int k = 0; k < tailLen; k++)
            output.Add(src[literalStart + k]);

        return output.ToArray();
    }
}