using System;
using System.IO;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Minimal, dependency-free image header reader. Extracts pixel dimensions from the
/// first bytes of a JPEG or PNG stream without decoding the whole image (and without
/// pulling in SkiaSharp/ImageSharp). Used to filter low-resolution AnimeClick posters.
/// </summary>
public static class ImageDimensions
{
    /// <summary>
    /// Tries to read width/height from an image stream (JPEG or PNG). Returns false
    /// when the format is unsupported or the header is truncated/corrupt.
    /// Safe to call with a short prefix stream (we only need the first ~20-100 bytes).
    /// </summary>
    public static bool TryRead(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var reader = new BinaryReader(stream);

            // Read enough bytes to recognise the signature.
            var sig = reader.ReadBytes(8);
            if (sig.Length < 8)
            {
                return false;
            }

            // PNG: 89 50 4E 47 0D 0A 1A 0A, then IHDR (width/height as big-endian int32).
            if (sig[0] == 0x89 && sig[1] == 0x50 && sig[2] == 0x4E && sig[3] == 0x47)
            {
                // Skip 4-byte chunk length + 4-byte "IHDR" type = 8 bytes.
                reader.ReadBytes(8);
                width = ReadBigEndianInt32(reader);
                height = ReadBigEndianInt32(reader);
                return width > 0 && height > 0;
            }

            // JPEG: starts with FF D8. We already consumed 8 bytes, so rewind to byte 2
            // and scan markers from there. Works even on non-seekable if we use a MemoryStream prefix.
            if (sig[0] == 0xFF && sig[1] == 0xD8)
            {
                if (stream.CanSeek)
                {
                    stream.Position = 2;
                }
                // If not seekable we are already at the right offset after reading sig (sig covered 0-7, position ~8).
                // For safety on prefix MemoryStream we reset to known good point.
                return TryReadJpeg(reader, out width, out height);
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadJpeg(BinaryReader reader, out int width, out int height)
    {
        width = 0;
        height = 0;

        while (true)
        {
            // Markers start with 0xFF; skip any fill bytes.
            int marker = reader.ReadByte();
            if (marker != 0xFF)
            {
                return false;
            }

            int code;
            do
            {
                code = reader.ReadByte();
            }
            while (code == 0xFF);

            // SOF markers carry the frame dimensions: C0-C3, C5-C7, C9-CB, CD-CF.
            // (C4=DHT, C8=JPG, CC=DAC are not start-of-frame.)
            bool isSof = (code >= 0xC0 && code <= 0xCF)
                && code != 0xC4 && code != 0xC8 && code != 0xCC;

            // Segment length (big-endian, includes the 2 length bytes).
            int len = (reader.ReadByte() << 8) | reader.ReadByte();
            if (len < 2)
            {
                return false;
            }

            if (isSof)
            {
                reader.ReadByte(); // precision
                height = (reader.ReadByte() << 8) | reader.ReadByte();
                width = (reader.ReadByte() << 8) | reader.ReadByte();
                return width > 0 && height > 0;
            }

            // Skip this segment's payload and continue to the next marker.
            reader.ReadBytes(len - 2);
        }
    }

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        var b = reader.ReadBytes(4);
        if (b.Length < 4)
        {
            return 0;
        }

        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }
}
