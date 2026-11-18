using System.Runtime.InteropServices;

namespace PDFStudio.Core;

/// <summary>
/// Fontes instaladas no Windows (a lista é rápida; se uma fonte serve para o PDF, quem diz é
/// <see cref="PdfPageContent.CanUseFont"/>, conferindo só a escolhida).
/// </summary>
public static class SystemFonts
{
    /// <summary>Famílias TrueType instaladas, em ordem alfabética (sem as variantes verticais "@" e sem nomes cortados pelo Windows).</summary>
    public static List<string> Installed()
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (!OperatingSystem.IsWindows()) return [.. names];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            var filter = new LOGFONT { lfCharSet = DEFAULT_CHARSET, lfFaceName = "" };
            EnumFontFamiliesEx(dc, ref filter, (ref ENUMLOGFONTEX font, IntPtr _, uint type, IntPtr _) =>
            {
                string name = font.elfLogFont.lfFaceName;
                if ((type & TRUETYPE_FONTTYPE) != 0 && name.Length is > 0 and < 31 && !name.StartsWith('@')) names.Add(name);
                return 1;
            }, IntPtr.Zero, 0);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        return [.. names];
    }

    // ---------------- GDI ----------------

    private const byte DEFAULT_CHARSET = 1;
    private const uint TRUETYPE_FONTTYPE = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUMLOGFONTEX
    {
        public LOGFONT elfLogFont;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string elfFullName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string elfStyle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string elfScript;
    }

    private delegate int EnumFontProc(ref ENUMLOGFONTEX font, IntPtr metric, uint type, IntPtr param);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(IntPtr dc, ref LOGFONT filter, EnumFontProc proc, IntPtr param, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
