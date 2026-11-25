using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using OpenConnect.Abstractions;

namespace OpenConnect.Privacy.Image;

/// <summary>Result of host-owned flattened raster image redaction (FW-040).</summary>
public sealed record ImageRedactionResult(
    bool Succeeded,
    byte[]? RedactedPngBytes,
    string? OutputSha256Digest,
    int Width,
    int Height,
    int RedactedRegionCount,
    bool MetadataStripped,
    string ReasonCode);

/// <summary>
/// Host-owned flattened raster image redactor (FW-040).
/// Decodes PNG with strict byte/megapixel decompression-bomb bounds, validates all region coordinates
/// against decoded dimensions, paints opaque masks with conservative padding, strips all metadata/ancillary
/// chunks and alpha layers, and re-encodes a new flattened 24-bit RGB PNG.
/// </summary>
public sealed class HostImageRedactor
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly int _maxImageBytes;
    private readonly int _maxMegapixels;
    private readonly int _defaultPaddingPixels;

    /// <summary>Creates a new <see cref="HostImageRedactor"/> with bounded resource limits.</summary>
    public HostImageRedactor(
        int maxImageBytes = 10 * 1024 * 1024,
        int maxMegapixels = 20,
        int defaultPaddingPixels = 4)
    {
        if (maxImageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxImageBytes));
        if (maxMegapixels <= 0) throw new ArgumentOutOfRangeException(nameof(maxMegapixels));
        if (defaultPaddingPixels < 0) throw new ArgumentOutOfRangeException(nameof(defaultPaddingPixels));

        _maxImageBytes = maxImageBytes;
        _maxMegapixels = maxMegapixels;
        _defaultPaddingPixels = defaultPaddingPixels;
    }

    /// <summary>
    /// Validates and redacts a PNG image using the supplied region findings.
    /// Fails closed on malformed input, oversized input, out-of-bounds regions, or failed recheck.
    /// </summary>
    public ImageRedactionResult RedactPng(
        ReadOnlySpan<byte> sourceBytes,
        IReadOnlyList<SecurityImageRegion> regions,
        int? paddingPixels = null,
        Func<ReadOnlyMemory<byte>, bool>? recheckCallback = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var padding = paddingPixels ?? _defaultPaddingPixels;
        if (padding < 0)
            return Fail("invalid_padding");
        if (sourceBytes.IsEmpty || sourceBytes.Length > _maxImageBytes)
            return Fail("image_limit_exceeded");
        if (regions.Count > 20_000)
            return Fail("too_many_regions");

        if (!TryDecodePngToRgb24(sourceBytes, out var width, out var height, out var rgbPixels, out var decodeError))
            return Fail(decodeError);

        // Validate ALL regions strictly against decoded dimensions before touching pixels.
        foreach (var region in regions)
        {
            if (region is null ||
                region.X < 0 || region.Y < 0 ||
                region.Width <= 0 || region.Height <= 0 ||
                (long)region.X + region.Width > width ||
                (long)region.Y + region.Height > height)
            {
                return Fail("invalid_region_bounds");
            }
        }

        // Paint opaque black (0, 0, 0) mask with conservative clamped padding.
        foreach (var region in regions)
        {
            var x0 = Math.Max(0, region.X - padding);
            var y0 = Math.Max(0, region.Y - padding);
            var x1 = Math.Min(width, region.X + region.Width + padding);
            var y1 = Math.Min(height, region.Y + region.Height + padding);

            for (var y = y0; y < y1; y++)
            {
                var rowStart = (y * width + x0) * 3;
                var byteCount = (x1 - x0) * 3;
                rgbPixels.AsSpan(rowStart, byteCount).Clear();
            }
        }

        // Re-encode strictly as a flattened 24-bit RGB PNG with ONLY IHDR, IDAT, IEND (all metadata stripped).
        var outputBytes = EncodeRgb24Png(width, height, rgbPixels);
        if (recheckCallback is not null && !recheckCallback(outputBytes))
            return Fail("redacted_output_recheck_failed");

        var digest = Convert.ToHexString(SHA256.HashData(outputBytes)).ToLowerInvariant();
        return new ImageRedactionResult(
            Succeeded: true,
            RedactedPngBytes: outputBytes,
            OutputSha256Digest: digest,
            Width: width,
            Height: height,
            RedactedRegionCount: regions.Count,
            MetadataStripped: true,
            ReasonCode: "redacted");
    }

    /// <summary>
    /// Redacts a PNG image using a structured <see cref="SecurityInspectionResponse"/>,
    /// enforcing required detector coverage and digest binding before redacting.
    /// </summary>
    public ImageRedactionResult RedactFromInspection(
        ReadOnlySpan<byte> sourceBytes,
        SecurityInspectionResponse inspection,
        IReadOnlySet<string>? requiredDetectors = null,
        int? paddingPixels = null,
        Func<ReadOnlyMemory<byte>, bool>? recheckCallback = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (inspection.Status != SecurityInspectionStatus.Completed)
            return Fail("inspection_incomplete");

        if (requiredDetectors is not null)
        {
            foreach (var req in requiredDetectors)
            {
                var det = inspection.Detectors.FirstOrDefault(d => string.Equals(d.Id, req, StringComparison.Ordinal));
                if (det is null || det.Status != SecurityInspectionStatus.Completed)
                    return Fail("required_coverage_missing");
            }
        }

        var actualDigest = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        if (!string.IsNullOrEmpty(inspection.ContentDigest) &&
            !string.Equals(actualDigest, inspection.ContentDigest, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("digest_mismatch");
        }

        var regions = new List<SecurityImageRegion>();
        foreach (var finding in inspection.Findings)
        {
            if (finding.Kind == SecurityFindingKind.ImageRegion)
            {
                if (finding.Region is null)
                    return Fail("invalid_region_bounds");
                regions.Add(finding.Region);
            }
        }

        return RedactPng(sourceBytes, regions, paddingPixels, recheckCallback);
    }

    /// <summary>Encodes a flat 24-bit RGB buffer (<c>width * height * 3</c> bytes) as a metadata-free PNG.</summary>
    public static byte[] EncodeRgb24Png(int width, int height, ReadOnlySpan<byte> rgbPixels)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        var expectedBytes = checked(width * height * 3);
        if (rgbPixels.Length != expectedBytes)
            throw new ArgumentException($"Expected {expectedBytes} RGB bytes.", nameof(rgbPixels));

        using var ms = new MemoryStream();
        ms.Write(PngSignature);

        // IHDR: width(4), height(4), bitDepth(1)=8, colorType(1)=2(RGB), compression(1)=0, filter(1)=0, interlace(1)=0
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(ms, "IHDR"u8, ihdr);

        // IDAT: zlib-compressed scanlines (filter byte 0 + RGB row)
        using var idatStream = new MemoryStream();
        using (var zlib = new ZLibStream(idatStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            var rowBytes = width * 3;
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // Filter type 0 (None)
                zlib.Write(rgbPixels.Slice(y * rowBytes, rowBytes));
            }
        }
        WriteChunk(ms, "IDAT"u8, idatStream.ToArray());
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private bool TryDecodePngToRgb24(ReadOnlySpan<byte> data, out int width, out int height, out byte[] rgb, out string error)
    {
        width = 0;
        height = 0;
        rgb = Array.Empty<byte>();
        error = "malformed_png";

        if (data.Length < 33 || !data[..8].SequenceEqual(PngSignature))
            return false;

        var offset = 8;
        var seenIhdr = false;
        var seenIend = false;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        byte[]? palette = null;
        using var compressedIdat = new MemoryStream();

        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > data.Length)
                return false;

            var chunkType = data.Slice(offset + 4, 4);
            var chunkData = data.Slice(offset + 8, (int)length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + (int)length, 4));
            var actualCrc = ComputeCrc(chunkType, chunkData);
            if (expectedCrc != actualCrc)
            {
                error = "png_crc_mismatch";
                return false;
            }

            offset += 12 + (int)length;

            if (chunkType.SequenceEqual("IHDR"u8))
            {
                if (seenIhdr || chunkData.Length != 13)
                    return false;
                seenIhdr = true;
                width = BinaryPrimitives.ReadInt32BigEndian(chunkData[0..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(chunkData[4..8]);
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                var compression = chunkData[10];
                var filter = chunkData[11];
                interlace = chunkData[12];

                if (width <= 0 || height <= 0 || compression != 0 || filter != 0 || interlace != 0)
                {
                    error = "unsupported_png_format";
                    return false;
                }
                if ((long)width * height > (long)_maxMegapixels * 1_000_000L)
                {
                    error = "image_megapixel_limit_exceeded";
                    return false;
                }
                if (bitDepth != 8 || colorType is not (0 or 2 or 3 or 4 or 6))
                {
                    error = "unsupported_png_format";
                    return false;
                }
            }
            else if (chunkType.SequenceEqual("PLTE"u8))
            {
                if (!seenIhdr || chunkData.Length == 0 || chunkData.Length % 3 != 0 || chunkData.Length > 768)
                    return false;
                palette = chunkData.ToArray();
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                if (!seenIhdr)
                    return false;
                if (compressedIdat.Length + chunkData.Length > _maxImageBytes)
                {
                    error = "image_limit_exceeded";
                    return false;
                }
                compressedIdat.Write(chunkData);
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                seenIend = true;
                break;
            }
            // All ancillary chunks (eXIf, tEXt, zTXt, iTXt, tIME, pHYs, etc.) are intentionally ignored & stripped.
        }

        if (!seenIhdr || !seenIend || compressedIdat.Length == 0)
            return false;
        if (colorType == 3 && palette is null)
            return false;

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0
        };

        var stride = checked(width * channels);
        var expectedDecompressed = checked((long)height * (stride + 1));
        if (expectedDecompressed > 256L * 1024 * 1024)
        {
            error = "decompression_limit_exceeded";
            return false;
        }

        var raw = new byte[expectedDecompressed];
        compressedIdat.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressedIdat, CompressionMode.Decompress);
            var total = 0;
            while (total < raw.Length)
            {
                var read = zlib.Read(raw, total, raw.Length - total);
                if (read == 0) break;
                total += read;
            }
            if (total != raw.Length)
                return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        // Unfilter scanlines
        var prevRow = new byte[stride];
        var currRow = new byte[stride];
        rgb = new byte[checked(width * height * 3)];

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * (stride + 1);
            var filterType = raw[rowOffset];
            var src = raw.AsSpan(rowOffset + 1, stride);

            for (var i = 0; i < stride; i++)
            {
                var a = i >= channels ? currRow[i - channels] : (byte)0;
                var b = prevRow[i];
                var c = i >= channels ? prevRow[i - channels] : (byte)0;
                currRow[i] = filterType switch
                {
                    0 => src[i],
                    1 => (byte)(src[i] + a),
                    2 => (byte)(src[i] + b),
                    3 => (byte)(src[i] + ((a + b) >> 1)),
                    4 => (byte)(src[i] + PaethPredictor(a, b, c)),
                    _ => throw new InvalidDataException("Invalid PNG filter.")
                };
            }

            // Convert to flattened opaque 24-bit RGB (compositing alpha over white background)
            var dstRow = y * width * 3;
            for (var x = 0; x < width; x++)
            {
                var dstIdx = dstRow + x * 3;
                switch (colorType)
                {
                    case 0: // Grayscale
                        rgb[dstIdx] = rgb[dstIdx + 1] = rgb[dstIdx + 2] = currRow[x];
                        break;
                    case 2: // RGB
                        rgb[dstIdx] = currRow[x * 3];
                        rgb[dstIdx + 1] = currRow[x * 3 + 1];
                        rgb[dstIdx + 2] = currRow[x * 3 + 2];
                        break;
                    case 3: // Indexed palette
                        var pIdx = currRow[x] * 3;
                        if (pIdx + 2 >= palette!.Length)
                            return false;
                        rgb[dstIdx] = palette[pIdx];
                        rgb[dstIdx + 1] = palette[pIdx + 1];
                        rgb[dstIdx + 2] = palette[pIdx + 2];
                        break;
                    case 4: // Grayscale + Alpha (flatten over white)
                        var g = currRow[x * 2];
                        var ga = currRow[x * 2 + 1];
                        var flatG = (byte)((g * ga + 255 * (255 - ga)) / 255);
                        rgb[dstIdx] = rgb[dstIdx + 1] = rgb[dstIdx + 2] = flatG;
                        break;
                    case 6: // RGBA (flatten over white)
                        var r = currRow[x * 4];
                        var gr = currRow[x * 4 + 1];
                        var bl = currRow[x * 4 + 2];
                        var al = currRow[x * 4 + 3];
                        rgb[dstIdx] = (byte)((r * al + 255 * (255 - al)) / 255);
                        rgb[dstIdx + 1] = (byte)((gr * al + 255 * (255 - al)) / 255);
                        rgb[dstIdx + 2] = (byte)((bl * al + 255 * (255 - al)) / 255);
                        break;
                }
            }

            Buffer.BlockCopy(currRow, 0, prevRow, 0, stride);
        }

        error = string.Empty;
        return true;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        stream.Write(lenBytes);
        stream.Write(type);
        stream.Write(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ComputeCrc(type, data));
        stream.Write(crcBytes);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static ImageRedactionResult Fail(string reasonCode) =>
        new(false, null, null, 0, 0, 0, false, reasonCode);
}
