using System.Text;

namespace _3DS.Core.FileSystem;

public static class SmdhWriter
{
    private const uint SmdhMagic = 0x48444D53;
    private const int TitleStructSize = 0x200;
    private const int ShortDescMaxBytes = 0x80;   // 0x40 UTF-16 문자
    private const int LongDescMaxBytes = 0x100;   // 0x80 UTF-16 문자
    private const int PublisherMaxBytes = 0x80;   // 0x40 UTF-16 문자

    public static byte[]? ApplyOverride(byte[] smdhData, string? shortDescription, string? publisher)
    {
        if (string.IsNullOrEmpty(shortDescription) && string.IsNullOrEmpty(publisher))
            return null;

        if (smdhData.Length < 0x36C0)
            return null;

        uint magic = BitConverter.ToUInt32(smdhData, 0);

        if (magic != SmdhMagic)
            return null;

        byte[] result = (byte[])smdhData.Clone();
        bool changed = false;

        for (int i = 0; i < 16; i++)
        {
            int offset = 0x08 + i * TitleStructSize;

            if (!HasContent(result, offset + 0x000, ShortDescMaxBytes))
                continue;

            if (!string.IsNullOrEmpty(shortDescription))
            {
                string current = ReadUtf16(result, offset + 0x000, ShortDescMaxBytes);

                if (!string.Equals(current, shortDescription, StringComparison.Ordinal))
                {
                    WriteUtf16(result, offset + 0x000, ShortDescMaxBytes, shortDescription);
                    WriteUtf16(result, offset + 0x080, LongDescMaxBytes, shortDescription);
                    changed = true;
                }
            }

            if (!string.IsNullOrEmpty(publisher))
            {
                string currentPublisher = ReadUtf16(result, offset + 0x180, PublisherMaxBytes);

                if (!string.Equals(currentPublisher, publisher, StringComparison.Ordinal))
                {
                    WriteUtf16(result, offset + 0x180, PublisherMaxBytes, publisher);
                    changed = true;
                }
            }
        }

        return changed ? result : null;
    }

    private static bool HasContent(byte[] data, int offset, int maxBytes)
    {
        int limit = Math.Min(offset + maxBytes, data.Length);

        for (int i = offset; i < limit - 1; i += 2)
        {
            if (data[i] != 0 || data[i + 1] != 0)
                return true;
        }

        return false;
    }

    private static string ReadUtf16(byte[] data, int offset, int maxBytes)
    {
        int limit = Math.Min(offset + maxBytes, data.Length);
        int end = offset;

        for (int i = offset; i + 1 < limit; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
                break;

            end = i + 2;
        }

        return Encoding.Unicode.GetString(data, offset, end - offset);
    }

    private static void WriteUtf16(byte[] data, int offset, int maxBytes, string text)
    {
        Array.Clear(data, offset, maxBytes);

        int maxChars = maxBytes / 2 - 1;
        string trimmed = text.Length > maxChars ? text[..maxChars] : text;
        byte[] encoded = Encoding.Unicode.GetBytes(trimmed);

        Array.Copy(encoded, 0, data, offset, Math.Min(encoded.Length, maxBytes - 2));
    }
}