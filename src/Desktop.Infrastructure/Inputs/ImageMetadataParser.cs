using System.Buffers.Binary;

namespace Desktop.Infrastructure.Inputs;

internal sealed record ParsedImageMetadata(string Format, string MimeType, int Width, int Height);

internal static class ImageMetadataParser
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ParsedImageMetadata Parse(Stream stream, string extension)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidDataException("INPUT_IMAGE_STREAM_INVALID");
        }

        stream.Position = 0;
        Span<byte> signature = stackalloc byte[8];
        ReadExactly(stream, signature);
        stream.Position = 0;

        ParsedImageMetadata metadata;
        if (signature[0] == 0xff && signature[1] == 0xd8)
        {
            metadata = ParseJpeg(stream);
        }
        else if (signature.SequenceEqual(PngSignature))
        {
            metadata = ParsePng(stream);
        }
        else if ((signature[0] == (byte)'I' && signature[1] == (byte)'I')
                 || (signature[0] == (byte)'M' && signature[1] == (byte)'M'))
        {
            metadata = ParseTiff(stream);
        }
        else
        {
            throw new InvalidDataException("INPUT_IMAGE_FORMAT_UNSUPPORTED");
        }

        var normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        var matches = metadata.Format switch
        {
            "jpeg" => normalizedExtension is "jpg" or "jpeg",
            "png" => normalizedExtension == "png",
            "tiff" => normalizedExtension is "tif" or "tiff",
            _ => false
        };
        if (!matches)
        {
            throw new InvalidDataException("INPUT_IMAGE_EXTENSION_MISMATCH");
        }

        return metadata;
    }

    private static ParsedImageMetadata ParseJpeg(Stream stream)
    {
        if (ReadByte(stream) != 0xff || ReadByte(stream) != 0xd8)
        {
            throw new InvalidDataException("INPUT_JPEG_INVALID");
        }

        int width = 0;
        int height = 0;
        var sawEnd = false;
        while (stream.Position < stream.Length)
        {
            if (ReadByte(stream) != 0xff)
            {
                throw new InvalidDataException("INPUT_JPEG_INVALID");
            }

            int marker;
            do
            {
                marker = ReadByte(stream);
            }
            while (marker == 0xff);

            if (marker == 0xd9)
            {
                sawEnd = true;
                break;
            }

            if (marker is 0xd8 or 0x01 || marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            var segmentLength = ReadUInt16BigEndian(stream);
            if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
            {
                throw new InvalidDataException("INPUT_JPEG_TRUNCATED");
            }

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 8)
                {
                    throw new InvalidDataException("INPUT_JPEG_INVALID");
                }

                _ = ReadByte(stream);
                height = ReadUInt16BigEndian(stream);
                width = ReadUInt16BigEndian(stream);
                stream.Position += segmentLength - 7;
                continue;
            }

            if (marker == 0xda)
            {
                stream.Position += segmentLength - 2;
                sawEnd = ScanJpegEntropyToEnd(stream);
                break;
            }

            stream.Position += segmentLength - 2;
        }

        if (!sawEnd || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("INPUT_JPEG_INVALID");
        }

        return new ParsedImageMetadata("jpeg", "image/jpeg", width, height);
    }

    private static bool ScanJpegEntropyToEnd(Stream stream)
    {
        while (stream.Position < stream.Length)
        {
            if (ReadByte(stream) != 0xff)
            {
                continue;
            }

            int marker;
            do
            {
                marker = ReadByte(stream);
            }
            while (marker == 0xff);

            if (marker == 0x00 || marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (marker == 0xd9)
            {
                return true;
            }

            throw new InvalidDataException("INPUT_JPEG_INVALID");
        }

        return false;
    }

    private static ParsedImageMetadata ParsePng(Stream stream)
    {
        Span<byte> signature = stackalloc byte[8];
        ReadExactly(stream, signature);
        if (!signature.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("INPUT_PNG_INVALID");
        }

        int width = 0;
        int height = 0;
        var sawHeader = false;
        var sawData = false;
        var sawEnd = false;
        var buffer = new byte[64 * 1024];
        Span<byte> type = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            var length = ReadUInt32BigEndian(stream);
            ReadExactly(stream, type);
            if (length > int.MaxValue || stream.Position + length + 4 > stream.Length)
            {
                throw new InvalidDataException("INPUT_PNG_TRUNCATED");
            }

            var crc = Crc32.Start();
            crc = Crc32.Update(crc, type);
            byte[]? header = null;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || length != 13 || sawData)
                {
                    throw new InvalidDataException("INPUT_PNG_INVALID");
                }
                header = new byte[13];
            }

            var remaining = (long)length;
            var headerOffset = 0;
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                ReadExactly(stream, buffer.AsSpan(0, count));
                crc = Crc32.Update(crc, buffer.AsSpan(0, count));
                if (header is not null)
                {
                    buffer.AsSpan(0, count).CopyTo(header.AsSpan(headerOffset));
                    headerOffset += count;
                }
                remaining -= count;
            }

            var expectedCrc = ReadUInt32BigEndian(stream);
            if (Crc32.Finish(crc) != expectedCrc)
            {
                throw new InvalidDataException("INPUT_PNG_CRC_INVALID");
            }

            if (header is not null)
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)));
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0)
                {
                    throw new InvalidDataException("INPUT_PNG_INVALID");
                }
                sawEnd = true;
                break;
            }
        }

        if (!sawHeader || !sawData || !sawEnd || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("INPUT_PNG_INVALID");
        }

        return new ParsedImageMetadata("png", "image/png", width, height);
    }

    private static ParsedImageMetadata ParseTiff(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        ReadExactly(stream, header);
        var littleEndian = header[0] == (byte)'I' && header[1] == (byte)'I';
        var bigEndian = header[0] == (byte)'M' && header[1] == (byte)'M';
        if (!littleEndian && !bigEndian)
        {
            throw new InvalidDataException("INPUT_TIFF_INVALID");
        }

        if (ReadUInt16(header[2..4], littleEndian) != 42)
        {
            throw new InvalidDataException("INPUT_TIFF_UNSUPPORTED");
        }

        var ifdOffset = ReadUInt32(header[4..8], littleEndian);
        if (ifdOffset < 8 || ifdOffset + 2 > stream.Length)
        {
            throw new InvalidDataException("INPUT_TIFF_TRUNCATED");
        }

        stream.Position = ifdOffset;
        Span<byte> countBytes = stackalloc byte[2];
        ReadExactly(stream, countBytes);
        var entryCount = ReadUInt16(countBytes, littleEndian);
        if (entryCount == 0 || entryCount > 4096 || stream.Position + (long)entryCount * 12 + 4 > stream.Length)
        {
            throw new InvalidDataException("INPUT_TIFF_INVALID");
        }

        uint width = 0;
        uint height = 0;
        uint[]? stripOffsets = null;
        uint[]? stripByteCounts = null;
        uint[]? tileOffsets = null;
        uint[]? tileByteCounts = null;
        Span<byte> entry = stackalloc byte[12];
        for (var index = 0; index < entryCount; index++)
        {
            ReadExactly(stream, entry);
            var tag = ReadUInt16(entry[..2], littleEndian);
            if (tag is not (256 or 257 or 273 or 279 or 324 or 325))
            {
                continue;
            }

            var type = ReadUInt16(entry[2..4], littleEndian);
            var valueCount = ReadUInt32(entry[4..8], littleEndian);
            if (type is not 3 and not 4)
            {
                throw new InvalidDataException("INPUT_TIFF_STORAGE_INVALID");
            }

            var values = ReadTiffUnsignedValues(stream, entry, littleEndian, type, valueCount);
            if (tag is 256 or 257)
            {
                if (values.Length != 1)
                {
                    throw new InvalidDataException("INPUT_TIFF_DIMENSIONS_INVALID");
                }

                if (tag == 256)
                {
                    width = values[0];
                }
                else
                {
                    height = values[0];
                }
            }
            else
            {
                switch (tag)
                {
                    case 273:
                        stripOffsets = values;
                        break;
                    case 279:
                        stripByteCounts = values;
                        break;
                    case 324:
                        tileOffsets = values;
                        break;
                    case 325:
                        tileByteCounts = values;
                        break;
                }
            }
        }

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidDataException("INPUT_TIFF_DIMENSIONS_INVALID");
        }

        var hasValidStrips = ValidateTiffPayload(stream.Length, stripOffsets, stripByteCounts);
        var hasValidTiles = ValidateTiffPayload(stream.Length, tileOffsets, tileByteCounts);
        if (!hasValidStrips && !hasValidTiles)
        {
            throw new InvalidDataException("INPUT_TIFF_STORAGE_INVALID");
        }

        return new ParsedImageMetadata("tiff", "image/tiff", (int)width, (int)height);
    }

    private static uint[] ReadTiffUnsignedValues(
        Stream stream,
        ReadOnlySpan<byte> entry,
        bool littleEndian,
        ushort type,
        uint valueCount)
    {
        if (valueCount == 0 || valueCount > 1_000_000)
        {
            throw new InvalidDataException("INPUT_TIFF_STORAGE_INVALID");
        }

        var elementSize = type == 3 ? 2 : 4;
        var byteCount = checked((long)valueCount * elementSize);
        var values = new uint[checked((int)valueCount)];
        if (byteCount <= 4)
        {
            ReadTiffValues(entry[8..12], values, littleEndian, type);
            return values;
        }

        var valueOffset = ReadUInt32(entry[8..12], littleEndian);
        if (valueOffset > stream.Length || byteCount > stream.Length - valueOffset)
        {
            throw new InvalidDataException("INPUT_TIFF_TRUNCATED");
        }

        var returnPosition = stream.Position;
        stream.Position = valueOffset;
        var bytes = new byte[checked((int)byteCount)];
        ReadExactly(stream, bytes);
        stream.Position = returnPosition;
        ReadTiffValues(bytes, values, littleEndian, type);
        return values;
    }

    private static void ReadTiffValues(
        ReadOnlySpan<byte> bytes,
        Span<uint> values,
        bool littleEndian,
        ushort type)
    {
        var elementSize = type == 3 ? 2 : 4;
        for (var index = 0; index < values.Length; index++)
        {
            var valueBytes = bytes.Slice(index * elementSize, elementSize);
            values[index] = type == 3
                ? ReadUInt16(valueBytes, littleEndian)
                : ReadUInt32(valueBytes, littleEndian);
        }
    }

    private static bool ValidateTiffPayload(
        long streamLength,
        uint[]? offsets,
        uint[]? byteCounts)
    {
        if (offsets is null || byteCounts is null || offsets.Length == 0 || offsets.Length != byteCounts.Length)
        {
            return false;
        }

        for (var index = 0; index < offsets.Length; index++)
        {
            var offset = offsets[index];
            var byteCount = byteCounts[index];
            if (byteCount == 0 || offset > streamLength || byteCount > streamLength - offset)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStartOfFrame(int marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7
        or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static int ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value >= 0 ? value : throw new EndOfStreamException("INPUT_IMAGE_TRUNCATED");
    }

    private static ushort ReadUInt16BigEndian(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        ReadExactly(stream, bytes);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExactly(stream, bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException("INPUT_IMAGE_TRUNCATED");
            }
            total += read;
        }
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Start() => uint.MaxValue;

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }
            return crc;
        }

        public static uint Finish(uint crc) => ~crc;

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1));
                }
                table[index] = value;
            }
            return table;
        }
    }
}
