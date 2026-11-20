namespace PDFStudio.Core;

/// <summary>Contas nos pixels das páginas que vão para a impressora (formato BGRA, 4 bytes por pixel, de cima para baixo).</summary>
public static class PrintBitmap
{
    /// <summary>Gira 90° no sentido horário. A largura nova é a altura antiga.</summary>
    public static byte[] Rotate90(byte[] bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("Imagem menor que o tamanho informado.");
        var result = new byte[width * height * 4];
        int newWidth = height;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int from = (y * width + x) * 4;
            int to = (x * newWidth + (newWidth - 1 - y)) * 4; // (x, y) vai para (altura-1-y, x)
            Buffer.BlockCopy(bgra, from, result, to, 4);
        }
        return result;
    }

    /// <summary>Troca as cores por tons de cinza (mesmo brilho), no próprio vetor.</summary>
    public static void ToGray(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte g = (byte)Math.Round(bgra[i] * 0.114 + bgra[i + 1] * 0.587 + bgra[i + 2] * 0.299);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = g;
        }
    }

    /// <summary>
    /// Passa para 3 bytes por pixel (BGR), com cada linha completada até múltiplo de 4 bytes, como o Windows pede.
    /// Fica 25% menor na fila da impressora.
    /// </summary>
    public static byte[] ToBgr24(byte[] bgra, int width, int height, out int stride)
    {
        stride = (width * 3 + 3) & ~3;
        var result = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int from = y * width * 4, to = y * stride;
            for (int x = 0; x < width; x++, from += 4, to += 3)
            {
                result[to] = bgra[from];
                result[to + 1] = bgra[from + 1];
                result[to + 2] = bgra[from + 2];
            }
        }
        return result;
    }
}
