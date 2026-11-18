using System.Buffers.Binary;
using System.IO.Compression;

namespace PDFStudio.Tests;

/// <summary>Cria imagens PNG simples para os testes (sem depender de arquivos externos).</summary>
public static class TestImages
{
    /// <summary>
    /// PNG <paramref name="width"/> x <paramref name="height"/>: metade de cima vermelha, metade de baixo azul
    /// (assim dá para conferir se a imagem ficou de pé). <paramref name="dpi"/> grava a resolução no arquivo.
    /// </summary>
    public static string Png(string path, int width, int height, int dpi = 0)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filtro "nenhum"
            for (int x = 0; x < width; x++)
            {
                bool top = y < height / 2;
                raw.Write(top ? [220, 30, 30] : [30, 60, 220]);
            }
        }
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(z);

        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits por canal
        header[9] = 2; // RGB
        Chunk(file, "IHDR", header);
        if (dpi > 0)
        {
            var phys = new byte[9];
            int perMeter = (int)Math.Round(dpi / 0.0254);
            BinaryPrimitives.WriteInt32BigEndian(phys, perMeter);
            BinaryPrimitives.WriteInt32BigEndian(phys.AsSpan(4), perMeter);
            phys[8] = 1;
            Chunk(file, "pHYs", phys);
        }
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
        return path;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        s.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        s.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
