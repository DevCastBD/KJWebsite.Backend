namespace SponsorshipService.Students;

/// <summary>
/// Removes location and other embedded metadata (EXIF, XMP, IPTC, comments, text chunks) from a JPEG or PNG without decoding the pixels
/// (D11). It walks the file structure, so a file that is not a well-formed JPEG or PNG is refused rather than passed through.
/// </summary>
public static class ImageSanitizer
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly HashSet<string> DroppedPngChunks = ["eXIf", "tEXt", "zTXt", "iTXt", "tIME"];

    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> head) => contentType switch
    {
        Jpeg => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        Png => head.StartsWith(PngSignature),
        "application/pdf" => head.StartsWith("%PDF-"u8),
        _ => false
    };

    /// <summary>The cleaned bytes, or null when the file is not a well-formed image of the declared type.</summary>
    public static byte[]? Strip(string contentType, byte[] data) => contentType switch
    {
        Jpeg => StripJpeg(data),
        Png => StripPng(data),
        _ => null
    };

    private static byte[]? StripJpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var position = 2;
        while (position < data.Length)
        {
            if (data[position] != 0xFF)
            {
                return null;
            }

            while (position < data.Length && data[position] == 0xFF)
            {
                position++;
            }

            if (position >= data.Length)
            {
                return null;
            }

            var marker = data[position];
            position++;
            if (marker == 0xD9)
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                return output.ToArray();
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                continue;
            }

            if (position + 2 > data.Length)
            {
                return null;
            }

            var length = (data[position] << 8) | data[position + 1];
            if (length < 2 || position + length > data.Length)
            {
                return null;
            }

            if (marker == 0xDA)
            {
                // Start of scan: the entropy-coded data follows to the end of the file and holds no metadata.
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                output.Write(data, position, data.Length - position);
                return output.ToArray();
            }

            // APP1 (EXIF, XMP), APP13 (IPTC, Photoshop) and comments are dropped; everything else is kept as it is.
            if (marker is not (0xE1 or 0xED or 0xFE))
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                output.Write(data, position, length);
            }

            position += length;
        }

        return null;
    }

    private static byte[]? StripPng(byte[] data)
    {
        if (data.Length < PngSignature.Length + 12 || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return null;
        }

        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, PngSignature.Length);
        var position = PngSignature.Length;
        var sawEnd = false;
        while (position < data.Length && !sawEnd)
        {
            if (position + 12 > data.Length)
            {
                return null;
            }

            var length = (long)(((uint)data[position] << 24) | ((uint)data[position + 1] << 16) | ((uint)data[position + 2] << 8) | data[position + 3]);
            var total = length + 12;
            if (length > int.MaxValue || position + total > data.Length)
            {
                return null;
            }

            var type = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            sawEnd = type == "IEND";
            if (!DroppedPngChunks.Contains(type))
            {
                output.Write(data, position, (int)total);
            }

            position += (int)total;
        }

        return sawEnd ? output.ToArray() : null;
    }
}
