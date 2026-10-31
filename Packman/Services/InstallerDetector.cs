using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Packman.Services;

public record InstallerDetection(InstallerKind Kind, Confidence? Confidence, string Evidence);

/// <summary>
/// Names an EXE's installer technology without running it. Reads only the PE headers, the
/// section table and four bounded areas (see <see cref="MarkerArea"/>), then matches them
/// against <see cref="InstallerCatalog"/>.
/// </summary>
public static class InstallerDetector
{
    private const int AreaLimit = 64 * 1024;
    private const int ResourceLimit = 4 * 1024 * 1024;
    private const int SectionEntrySize = 40;

    public static InstallerDetection Detect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Detect(stream);
    }

    public static InstallerDetection Detect(Stream stream)
    {
        var areas = ReadAreas(stream);

        InstallerDetection best = new(InstallerCatalog.Unknown, null, "");
        foreach (var kind in InstallerCatalog.Kinds)
        {
            foreach (var marker in kind.Markers)
            {
                if (best.Confidence >= marker.Confidence || !Matches(areas, marker)) continue;
                best = new InstallerDetection(kind, marker.Confidence, Evidence(marker));
            }
        }
        return best;
    }

    private static string Evidence(Marker marker) =>
        $"{marker.Area.ToString().ToLowerInvariant()} {new string(marker.Text.Where(c => c is >= ' ' and <= '~').ToArray())}";

    private sealed record Areas(List<string> SectionNames, byte[] Resources, byte[] Overlay, byte[] Tail);

    private static bool Matches(Areas areas, Marker marker)
    {
        if (marker.Area == MarkerArea.Section) return areas.SectionNames.Contains(marker.Text);

        var haystack = marker.Area switch
        {
            MarkerArea.Resources => areas.Resources,
            MarkerArea.Overlay => areas.Overlay,
            _ => areas.Tail,
        };
        return haystack.AsSpan().IndexOf(Encoding.Latin1.GetBytes(marker.Text)) >= 0
            || haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(marker.Text)) >= 0;
    }

    private static Areas ReadAreas(Stream stream)
    {
        var length = stream.Length;
        var dos = ReadAt(stream, 0, 64);
        if (dos.Length < 64 || dos[0] != 'M' || dos[1] != 'Z')
            throw new InvalidDataException("not a Windows executable (no MZ header).");

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        if (peOffset < 64 || peOffset > length - 24)
            throw new InvalidDataException("the PE header offset is outside the file.");

        var coff = ReadAt(stream, peOffset, 24);
        if (coff.Length < 24 || coff[0] != 'P' || coff[1] != 'E' || coff[2] != 0 || coff[3] != 0)
            throw new InvalidDataException("not a Windows executable (no PE signature).");

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
        var optionalStart = peOffset + 24L;

        var sections = ReadAt(stream, optionalStart + optionalSize, sectionCount * SectionEntrySize);
        var names = new List<string>();
        byte[] resources = [];
        long overlayStart = 0;
        for (var i = 0; i + SectionEntrySize <= sections.Length; i += SectionEntrySize)
        {
            var entry = sections.AsSpan(i, SectionEntrySize);
            var name = Encoding.Latin1.GetString(entry[..8]).TrimEnd('\0');
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
            names.Add(name);
            if (rawSize > 0) overlayStart = Math.Max(overlayStart, (long)rawOffset + rawSize);
            if (name == ".rsrc" && resources.Length == 0)
                resources = ReadAt(stream, rawOffset, (int)Math.Min(rawSize, ResourceLimit));
        }

        // Past EOF means no overlay. The certificate table is signing data, not installer data.
        overlayStart = Math.Min(overlayStart, length);
        var end = length;
        var certificate = CertificateTableOffset(stream, optionalStart, optionalSize);
        if (certificate >= overlayStart && certificate < length) end = certificate;

        var overlay = ReadAt(stream, overlayStart, (int)Math.Min(AreaLimit, end - overlayStart));
        var tailStart = Math.Max(0, end - AreaLimit);
        var tail = ReadAt(stream, tailStart, (int)(end - tailStart));
        return new Areas(names, resources, overlay, tail);
    }

    /// <summary>File offset of the Authenticode table (data directory 4), or 0 when unsigned.</summary>
    private static long CertificateTableOffset(Stream stream, long optionalStart, int optionalSize)
    {
        var header = ReadAt(stream, optionalStart, Math.Min(optionalSize, 160));
        if (header.Length < 2) return 0;

        var pe32Plus = BinaryPrimitives.ReadUInt16LittleEndian(header) == 0x20B;
        var directories = pe32Plus ? 112 : 96;
        var entry = directories + 4 * 8;
        if (header.Length < entry + 8 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entry + 4)) == 0) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entry));
    }

    /// <summary>Up to <paramref name="count"/> bytes at <paramref name="offset"/>; fewer when the file ends first.</summary>
    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (count <= 0 || offset < 0 || offset >= stream.Length) return [];
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
