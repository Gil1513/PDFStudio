using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PDFStudio.Core;

/// <summary>Uma impressora instalada. <see cref="Port"/> "PORTPROMPT:" = imprime num arquivo (ex.: Imprimir em PDF).</summary>
public sealed record PrinterInfo(string Name, string Port, bool IsDefault)
{
    /// <summary>A impressora grava um arquivo em vez de papel: o app pergunta onde (senão o Windows abre a janela dele).</summary>
    public bool WritesFile => Port.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extensão do arquivo que ela grava (só faz sentido com <see cref="WritesFile"/>).</summary>
    public string FileExtension =>
        Name.Contains("PDF", StringComparison.OrdinalIgnoreCase) ? ".pdf"
        : Name.Contains("XPS", StringComparison.OrdinalIgnoreCase) ? ".oxps" : ".prn";
}

/// <summary>Um tamanho de papel que a impressora aceita. Medidas em milímetros, em pé.</summary>
public sealed record PaperKind(short Id, string Name, double WidthMm, double HeightMm);

/// <summary>O que a impressora sabe fazer.</summary>
public sealed record PrinterCapabilities(IReadOnlyList<PaperKind> Papers, bool Duplex, bool Color);

/// <summary>
/// Impressoras do Windows sem a janela de impressão dele: lista, recursos, configurações (DEVMODE) e medidas da
/// folha. Usa só funções do próprio Windows (winspool.drv e gdi32.dll), sem pacotes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrinters
{
    public static List<PrinterInfo> List()
    {
        string? standard = DefaultName();
        const uint flags = Native.PRINTER_ENUM_LOCAL | Native.PRINTER_ENUM_CONNECTIONS;
        Native.EnumPrintersW(flags, null, 5, 0, 0, out uint needed, out _);
        if (needed == 0) return [];
        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Native.EnumPrintersW(flags, null, 5, buffer, needed, out _, out uint count)) return [];
            int size = Marshal.SizeOf<Native.PRINTER_INFO_5>();
            var result = new List<PrinterInfo>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<Native.PRINTER_INFO_5>(buffer + i * size);
                string name = Marshal.PtrToStringUni(info.pPrinterName) ?? "";
                if (name.Length == 0) continue;
                string port = Marshal.PtrToStringUni(info.pPortName) ?? "";
                result.Add(new PrinterInfo(name, port, string.Equals(name, standard, StringComparison.OrdinalIgnoreCase)));
            }
            return result.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Nome da impressora padrão do Windows (null se não houver).</summary>
    public static string? DefaultName()
    {
        int size = 0;
        Native.GetDefaultPrinterW(null, ref size);
        if (size <= 0) return null;
        var buffer = new char[size];
        return Native.GetDefaultPrinterW(buffer, ref size) ? new string(buffer, 0, Math.Max(0, size - 1)) : null;
    }

    /// <summary>A configuração atual da impressora (as preferências salvas no Windows).</summary>
    public static PrintDevMode DefaultDevMode(string printer) => CallDocumentProperties(0, printer, null, Native.DM_OUT_BUFFER)
        ?? throw new InvalidOperationException($"Não foi possível ler a configuração da impressora “{printer}”.");

    /// <summary>Passa a configuração pelo driver, que corrige o que a impressora não aceita.</summary>
    public static PrintDevMode Validate(string printer, PrintDevMode devMode) =>
        CallDocumentProperties(0, printer, devMode, Native.DM_IN_BUFFER | Native.DM_OUT_BUFFER) ?? devMode;

    /// <summary>
    /// Abre a janela de preferências do fabricante da impressora (opções que só ela tem). Devolve a configuração
    /// nova, ou null se a pessoa cancelou.
    /// </summary>
    public static PrintDevMode? EditPreferences(nint owner, string printer, PrintDevMode devMode) =>
        CallDocumentProperties(owner, printer, devMode, Native.DM_IN_BUFFER | Native.DM_OUT_BUFFER | Native.DM_IN_PROMPT);

    private static PrintDevMode? CallDocumentProperties(nint owner, string printer, PrintDevMode? input, uint mode)
    {
        if (!Native.OpenPrinterW(printer, out nint handle, 0)) return null;
        nint inBuffer = 0, outBuffer = 0;
        try
        {
            int size = Native.DocumentPropertiesW(owner, handle, printer, 0, 0, 0);
            if (size <= 0) return null;
            outBuffer = Marshal.AllocHGlobal(size);
            if (input is not null)
            {
                inBuffer = Marshal.AllocHGlobal(Math.Max(size, input.Bytes.Length));
                Marshal.Copy(input.Bytes, 0, inBuffer, input.Bytes.Length);
            }
            int result = Native.DocumentPropertiesW(owner, handle, printer, outBuffer, inBuffer, mode);
            if (result < 0 || ((mode & Native.DM_IN_PROMPT) != 0 && result != Native.IDOK)) return null;
            var bytes = new byte[size];
            Marshal.Copy(outBuffer, bytes, 0, size);
            return new PrintDevMode(bytes);
        }
        finally
        {
            if (inBuffer != 0) Marshal.FreeHGlobal(inBuffer);
            if (outBuffer != 0) Marshal.FreeHGlobal(outBuffer);
            Native.ClosePrinter(handle);
        }
    }

    /// <summary>Papéis, frente e verso e cor.</summary>
    public static PrinterCapabilities Capabilities(PrinterInfo printer, PrintDevMode? devMode)
    {
        using var dm = new PinnedDevMode(devMode);
        string name = printer.Name, port = printer.Port;
        var ids = Read(name, port, Native.DC_PAPERS, 2, dm.Pointer, (b, i) => BitConverter.ToInt16(b, i * 2));
        var names = Read(name, port, Native.DC_PAPERNAMES, 128, dm.Pointer, (b, i) =>
        {
            string s = System.Text.Encoding.Unicode.GetString(b, i * 128, 128);
            int end = s.IndexOf('\0');
            return end >= 0 ? s[..end] : s;
        });
        var sizes = Read(name, port, Native.DC_PAPERSIZE, 8, dm.Pointer, (b, i) => (BitConverter.ToInt32(b, i * 8), BitConverter.ToInt32(b, i * 8 + 4)));
        var papers = new List<PaperKind>();
        for (int i = 0; i < ids.Count && i < names.Count && i < sizes.Count; i++)
        {
            var (w, h) = sizes[i]; // décimos de milímetro
            if (w <= 0 || h <= 0 || string.IsNullOrWhiteSpace(names[i])) continue;
            papers.Add(new PaperKind(ids[i], names[i].Trim(), Math.Min(w, h) / 10.0, Math.Max(w, h) / 10.0));
        }
        bool duplex = Native.DeviceCapabilitiesW(name, port, Native.DC_DUPLEX, 0, dm.Pointer) == 1;
        bool color = Native.DeviceCapabilitiesW(name, port, Native.DC_COLORDEVICE, 0, dm.Pointer) == 1;
        return new PrinterCapabilities(papers, duplex, color);
    }

    private static List<T> Read<T>(string name, string port, ushort capability, int itemSize, nint devMode, Func<byte[], int, T> item)
    {
        int count = Native.DeviceCapabilitiesW(name, port, capability, 0, devMode);
        if (count <= 0 || count > 10000) return [];
        nint buffer = Marshal.AllocHGlobal(count * itemSize);
        try
        {
            int got = Native.DeviceCapabilitiesW(name, port, capability, buffer, devMode);
            if (got <= 0) return [];
            got = Math.Min(got, count);
            var bytes = new byte[got * itemSize];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return Enumerable.Range(0, got).Select(i => item(bytes, i)).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Medidas da folha com esta configuração (papel, orientação), sem imprimir nada.</summary>
    public static SheetMetrics Measure(string printer, PrintDevMode? devMode)
    {
        using var dm = new PinnedDevMode(devMode);
        nint dc = Native.CreateICW("WINSPOOL", printer, null, dm.Pointer);
        if (dc == 0) throw new InvalidOperationException($"Não foi possível acessar a impressora “{printer}”.");
        try
        {
            return Native.Metrics(dc);
        }
        finally
        {
            Native.DeleteDC(dc);
        }
    }
}

/// <summary>
/// Uma impressão em andamento: cada folha recebe uma imagem (ou fica em branco). <see cref="Finish"/> manda para
/// a fila; descartar sem terminar cancela tudo (nada sai da impressora).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrintSession : IDisposable
{
    private nint _dc;
    private bool _started, _finished;

    private WindowsPrintSession(nint dc) => _dc = dc;

    public SheetMetrics Sheet { get; private set; }

    /// <param name="outputFile">Para impressoras que gravam arquivo (Imprimir em PDF): onde gravar.</param>
    public static WindowsPrintSession Start(string printer, PrintDevMode? devMode, string title, string? outputFile)
    {
        nint dc;
        using (var dm = new PinnedDevMode(devMode))
            dc = Native.CreateDCW("WINSPOOL", printer, null, dm.Pointer);
        if (dc == 0) throw new InvalidOperationException($"Não foi possível abrir a impressora “{printer}”.");
        var session = new WindowsPrintSession(dc);
        try
        {
            session.Sheet = Native.Metrics(dc);
            var info = new Native.DOCINFOW
            {
                cbSize = Marshal.SizeOf<Native.DOCINFOW>(),
                lpszDocName = string.IsNullOrWhiteSpace(title) ? "Documento" : title,
                lpszOutput = outputFile,
            };
            if (Native.StartDocW(dc, ref info) <= 0)
                throw new InvalidOperationException("A impressora não aceitou o documento.");
            session._started = true;
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Uma folha em branco (o verso que fica vazio no frente e verso).</summary>
    public void AddBlankSheet()
    {
        Begin();
        End();
    }

    /// <summary>
    /// Uma folha com a imagem (BGRA, de cima para baixo) esticada no retângulo dado, em pixels da impressora
    /// contados do canto da área imprimível.
    /// </summary>
    public void AddImageSheet(byte[] bgra, int width, int height, int x, int y, int destWidth, int destHeight)
    {
        Begin();
        byte[] bgr = PrintBitmap.ToBgr24(bgra, width, height, out _);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // negativo = a primeira linha é a de cima
            biPlanes = 1,
            biBitCount = 24,
        };
        Native.SetStretchBltMode(_dc, Native.HALFTONE);
        Native.SetBrushOrgEx(_dc, 0, 0, 0);
        int lines = Native.StretchDIBits(_dc, x, y, destWidth, destHeight, 0, 0, width, height, bgr, ref header, 0, Native.SRCCOPY);
        if (lines <= 0) throw new InvalidOperationException("A impressora não aceitou a imagem da página.");
        End();
    }

    private void Begin()
    {
        if (_dc == 0 || _finished) throw new ObjectDisposedException(nameof(WindowsPrintSession));
        if (Native.StartPage(_dc) <= 0) throw new InvalidOperationException("A impressora não aceitou uma folha nova.");
    }

    private void End()
    {
        if (Native.EndPage(_dc) <= 0) throw new InvalidOperationException("A impressora não aceitou a folha.");
    }

    /// <summary>Termina e manda para a fila da impressora.</summary>
    public void Finish()
    {
        if (Native.EndDoc(_dc) <= 0) throw new InvalidOperationException("A impressora não aceitou o fim do documento.");
        _finished = true;
    }

    public void Dispose()
    {
        if (_dc == 0) return;
        if (_started && !_finished) Native.AbortDoc(_dc);
        Native.DeleteDC(_dc);
        _dc = 0;
    }
}

/// <summary>Cópia da configuração na memória do Windows enquanto uma chamada precisa dela.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PinnedDevMode : IDisposable
{
    public PinnedDevMode(PrintDevMode? devMode)
    {
        if (devMode is null) return;
        Pointer = Marshal.AllocHGlobal(devMode.Bytes.Length);
        Marshal.Copy(devMode.Bytes, 0, Pointer, devMode.Bytes.Length);
    }

    public nint Pointer { get; private set; }

    public void Dispose()
    {
        if (Pointer != 0) Marshal.FreeHGlobal(Pointer);
        Pointer = 0;
    }
}

[SupportedOSPlatform("windows")]
internal static class Native
{
    public const uint PRINTER_ENUM_LOCAL = 2, PRINTER_ENUM_CONNECTIONS = 4;
    public const uint DM_OUT_BUFFER = 2, DM_IN_PROMPT = 4, DM_IN_BUFFER = 8;
    public const int IDOK = 1;
    public const ushort DC_PAPERS = 2, DC_PAPERSIZE = 3, DC_DUPLEX = 7, DC_PAPERNAMES = 16, DC_COLORDEVICE = 32;
    public const int HORZRES = 8, VERTRES = 10, LOGPIXELSX = 88, LOGPIXELSY = 90,
        PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113;
    public const int HALFTONE = 4;
    public const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_INFO_5
    {
        public nint pPrinterName, pPortName;
        public uint Attributes, DeviceNotSelectedTimeout, TransmissionRetryTimeout;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOCINFOW
    {
        public int cbSize;
        public string lpszDocName;
        public string? lpszOutput;
        public string? lpszDatatype;
        public int fwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    public static SheetMetrics Metrics(nint dc) => new(
        GetDeviceCaps(dc, LOGPIXELSX), GetDeviceCaps(dc, LOGPIXELSY),
        GetDeviceCaps(dc, PHYSICALWIDTH), GetDeviceCaps(dc, PHYSICALHEIGHT),
        GetDeviceCaps(dc, PHYSICALOFFSETX), GetDeviceCaps(dc, PHYSICALOFFSETY),
        GetDeviceCaps(dc, HORZRES), GetDeviceCaps(dc, VERTRES));

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumPrintersW(uint flags, string? name, uint level, nint buffer, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDefaultPrinterW([Out] char[]? buffer, ref int size);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinterW(string name, out nint handle, nint defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(nint handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DocumentPropertiesW(nint owner, nint printer, string name, nint output, nint input, uint mode);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DeviceCapabilitiesW(string device, string? port, ushort capability, nint output, nint devMode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateDCW(string driver, string device, string? port, nint devMode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateICW(string driver, string device, string? port, nint devMode);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(nint dc, int index);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int StartDocW(nint dc, ref DOCINFOW info);

    [DllImport("gdi32.dll")]
    public static extern int StartPage(nint dc);

    [DllImport("gdi32.dll")]
    public static extern int EndPage(nint dc);

    [DllImport("gdi32.dll")]
    public static extern int EndDoc(nint dc);

    [DllImport("gdi32.dll")]
    public static extern int AbortDoc(nint dc);

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    public static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);

    [DllImport("gdi32.dll")]
    public static extern int StretchDIBits(nint dc, int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight, byte[] bits, ref BITMAPINFOHEADER info, uint usage, uint rop);
}
