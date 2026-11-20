namespace PDFStudio.Core;

/// <summary>
/// Configurações de impressão do Windows (estrutura DEVMODEW) guardadas como bytes. Cada impressora devolve a sua
/// (com uma parte extra só do fabricante no fim); aqui só mexemos nos campos padrão, nas posições fixas da estrutura.
/// Cada campo alterado também é marcado em dmFields, senão o driver ignora.
/// </summary>
public sealed class PrintDevMode
{
    // Posições (bytes) dentro de DEVMODEW: 32 letras do nome (64 bytes) e depois os campos.
    internal const int FieldsOffset = 72, OrientationOffset = 76, PaperSizeOffset = 78, CopiesOffset = 86,
        ColorOffset = 92, DuplexOffset = 94, CollateOffset = 100, MinimumSize = 102;
    internal const uint DmOrientation = 0x1, DmPaperSize = 0x2, DmCopies = 0x100, DmColor = 0x800, DmDuplex = 0x1000, DmCollate = 0x8000;

    public PrintDevMode(byte[] bytes)
    {
        if (bytes.Length < MinimumSize) throw new ArgumentException("Configuração de impressora inválida (pequena demais).");
        Bytes = bytes;
    }

    /// <summary>Os bytes (os mesmos que foram passados; as mudanças vão direto neles).</summary>
    public byte[] Bytes { get; }

    public PrintDevMode Clone() => new((byte[])Bytes.Clone());

    public uint Fields => BitConverter.ToUInt32(Bytes, FieldsOffset);

    /// <summary>Código do papel do Windows (1 = Carta, 9 = A4…); 0 se a impressora não informa.</summary>
    public short PaperSize
    {
        get => Get(PaperSizeOffset, DmPaperSize);
        set => Set(PaperSizeOffset, DmPaperSize, value);
    }

    /// <summary>1 = retrato, 2 = paisagem.</summary>
    public short Orientation
    {
        get => Get(OrientationOffset, DmOrientation);
        set => Set(OrientationOffset, DmOrientation, value);
    }

    public short Copies
    {
        get => Get(CopiesOffset, DmCopies);
        set => Set(CopiesOffset, DmCopies, value);
    }

    /// <summary>true = colorido, false = preto e branco.</summary>
    public bool Color
    {
        get => Get(ColorOffset, DmColor) != 1;
        set => Set(ColorOffset, DmColor, (short)(value ? 2 : 1));
    }

    /// <summary>1 = só frente, 2 = frente e verso virando na borda longa, 3 = na borda curta.</summary>
    public short Duplex
    {
        get => Get(DuplexOffset, DmDuplex) is var d && d is >= 1 and <= 3 ? d : (short)1;
        set => Set(DuplexOffset, DmDuplex, value);
    }

    public bool Collate
    {
        get => Get(CollateOffset, DmCollate) == 1;
        set => Set(CollateOffset, DmCollate, (short)(value ? 1 : 0));
    }

    private short Get(int offset, uint flag) => (Fields & flag) == 0 ? (short)0 : BitConverter.ToInt16(Bytes, offset);

    private void Set(int offset, uint flag, short value)
    {
        BitConverter.TryWriteBytes(Bytes.AsSpan(offset, 2), value);
        BitConverter.TryWriteBytes(Bytes.AsSpan(FieldsOffset, 4), Fields | flag);
    }
}
