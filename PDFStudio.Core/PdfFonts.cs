using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PdfSharp.Fonts;

namespace PDFStudio.Core;

/// <summary>
/// Ensina o PDFsharp a achar qualquer fonte instalada no Windows: o próprio Windows (GDI) entrega os bytes da
/// fonte pedida, já na variação certa (negrito/itálico). Sem isto o PDFsharp só conhece poucas fontes.
/// </summary>
public static class PdfFonts
{
    private static readonly object Gate = new();
    private static bool _done;

    /// <summary>Instala o localizador de fontes (uma vez só, antes de qualquer uso de fonte do PDFsharp).</summary>
    public static void Ensure()
    {
        FontLibrary.EnsureRegistered(); // fontes gêmeas e baixadas (Etapa 5.10), só para este programa
        lock (Gate)
        {
            if (_done) return;
            _done = true;
            if (GlobalFontSettings.FontResolver is not null) return;
            if (OperatingSystem.IsWindows()) GlobalFontSettings.FontResolver = new GdiFontResolver();
        }
    }

    /// <summary>Busca as fontes pelo GDI do Windows. Fontes CFF (.otf "PostScript") não servem para o PDFsharp e ficam de fora.</summary>
    private sealed class GdiFontResolver : IFontResolver
    {
        private readonly Dictionary<string, byte[]> _faces = new();
        private readonly Dictionary<(string, bool, bool), FontResolverInfo?> _resolved = new();

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            lock (_faces)
            {
                var key = (familyName.ToLowerInvariant(), isBold, isItalic);
                if (_resolved.TryGetValue(key, out var known)) return known;
                return _resolved[key] = Resolve(familyName, isBold, isItalic);
            }
        }

        public byte[]? GetFont(string faceName)
        {
            lock (_faces) return _faces.GetValueOrDefault(faceName);
        }

        private FontResolverInfo? Resolve(string family, bool bold, bool italic)
        {
            var data = Load(family, bold, italic);
            if (data is null) return null;
            // Se a fonte não tem a variação pedida, o Windows devolve a normal: então o PDFsharp "simula".
            bool simulateBold = bold && Load(family, false, italic) is { } noBold && Same(data, noBold);
            bool simulateItalic = italic && Load(family, bold, false) is { } noItalic && Same(data, noItalic);
            string face = $"{family}#{Convert.ToHexString(SHA1.HashData(data))[..16]}";
            _faces[face] = data;
            return new FontResolverInfo(face, simulateBold, simulateItalic);
        }

        private static bool Same(byte[] a, byte[] b) => a.Length == b.Length && a.AsSpan().SequenceEqual(b);
    }

    // ---------------- GDI ----------------

    private const uint TtcTag = 0x66637474; // 'ttcf' (bytes invertidos, como o GDI espera)

    /// <summary>Bytes de uma fonte TrueType independente (fontes de coleções .ttc são remontadas), ou null.</summary>
    private static byte[]? Load(string family, bool bold, bool italic)
    {
        if (family.Length is 0 or > 31) return null;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        var logFont = new LOGFONT { lfHeight = -100, lfWeight = bold ? 700 : 400, lfItalic = (byte)(italic ? 1 : 0), lfCharSet = 1, lfFaceName = family };
        IntPtr font = CreateFontIndirect(ref logFont);
        IntPtr old = SelectObject(dc, font);
        try
        {
            // O Windows troca por outra fonte quando o nome não existe: confere se é a pedida.
            var name = new char[64];
            int len = GetTextFace(dc, name.Length, name);
            if (len <= 1 || !string.Equals(new string(name, 0, len - 1), family, StringComparison.OrdinalIgnoreCase)) return null;

            uint size = GetFontData(dc, 0, 0, null, 0);
            if (size == uint.MaxValue || size < 12) return null;
            var data = new byte[size];
            if (GetFontData(dc, 0, 0, data, size) != size) return null;
            uint version = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (version != 0x00010000 && version != 0x74727565) return null; // só contornos TrueType (OTTO = CFF não serve)

            // Numa coleção (.ttc) as posições das tabelas contam do começo do arquivo inteiro: monta uma fonte avulsa.
            uint whole = GetFontData(dc, TtcTag, 0, null, 0);
            return whole != uint.MaxValue && whole > 0 ? Rebuild(dc, data) : data;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    /// <summary>Monta um arquivo TrueType só com as tabelas desta fonte (pedidas uma a uma ao GDI).</summary>
    private static byte[]? Rebuild(IntPtr dc, byte[] header)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (header.Length < 12 + count * 16) return null;
        var tables = new List<(uint Tag, uint Checksum, byte[] Data)>();
        for (int i = 0; i < count; i++)
        {
            var entry = header.AsSpan(12 + i * 16);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            uint gdiTag = BinaryPrimitives.ReverseEndianness(tag);
            uint size = GetFontData(dc, gdiTag, 0, null, 0);
            if (size == uint.MaxValue) return null;
            var data = new byte[size];
            if (size > 0 && GetFontData(dc, gdiTag, 0, data, size) != size) return null;
            tables.Add((tag, BinaryPrimitives.ReadUInt32BigEndian(entry[4..]), data));
        }
        int offset = 12 + count * 16;
        var result = new byte[offset + tables.Sum(t => (t.Data.Length + 3) & ~3)];
        header.AsSpan(0, 12).CopyTo(result);
        for (int i = 0; i < count; i++)
        {
            var (tag, checksum, data) = tables[i];
            var entry = result.AsSpan(12 + i * 16);
            BinaryPrimitives.WriteUInt32BigEndian(entry, tag);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], checksum);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)data.Length);
            data.CopyTo(result, offset);
            offset += (data.Length + 3) & ~3;
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontIndirect(ref LOGFONT font);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextFace(IntPtr dc, int count, [Out] char[] name);
    [DllImport("gdi32.dll")] private static extern uint GetFontData(IntPtr dc, uint table, uint offset, byte[]? buffer, uint length);
}
