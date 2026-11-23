namespace PDFStudio.Core;

// Ações de uma página só (menu do botão direito na página).
public static partial class PdfOperations
{
    /// <summary>
    /// Leva a página <paramref name="index"/> (0..n-1) para a posição <paramref name="newIndex"/>; as do meio
    /// andam uma casa. Pode sobrescrever o próprio arquivo de entrada.
    /// </summary>
    public static void MovePage(string input, int index, int newIndex, string output)
    {
        int total = GetPageCount(input);
        if (index < 0 || index >= total) throw new ArgumentOutOfRangeException(nameof(index), $"A página {index + 1} não existe.");
        if (newIndex < 0 || newIndex >= total) throw new ArgumentOutOfRangeException(nameof(newIndex), "Não dá para mover a página para lá.");
        var order = Enumerable.Range(0, total).ToList();
        order.RemoveAt(index);
        order.Insert(newIndex, index);
        Reorganize(input, order.Select(i => new PageEdit(i)).ToList(), output);
    }

    /// <summary>Gira só a página <paramref name="index"/> (graus no sentido horário, múltiplos de 90).</summary>
    public static void RotatePage(string input, int index, int degrees, string output)
    {
        int total = GetPageCount(input);
        if (index < 0 || index >= total) throw new ArgumentOutOfRangeException(nameof(index), $"A página {index + 1} não existe.");
        Reorganize(input, Enumerable.Range(0, total).Select(i => new PageEdit(i, i == index ? degrees : 0)).ToList(), output);
    }
}
