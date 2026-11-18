namespace PDFStudio.Core;

/// <summary>
/// Uma página no documento reorganizado: de qual página original ela vem (índice 0..n-1)
/// e quantos graus girar a mais (0, 90, 180, 270; sentido horário).
/// </summary>
public readonly record struct PageEdit(int SourceIndex, int Rotation = 0);

/// <summary>Orientação desejada ao redimensionar (como a página aparece na tela).</summary>
public enum PageOrientationChoice
{
    /// <summary>Mantém a orientação de cada página (deitada continua deitada).</summary>
    Auto,
    Portrait,
    Landscape,
}

/// <summary>Margens em pontos (1/72 pol.), medidas como a página aparece na tela.</summary>
public readonly record struct PageMargins(double Top, double Right, double Bottom, double Left)
{
    public const double PointsPerCm = 72 / 2.54;

    public static PageMargins FromCentimeters(double top, double right, double bottom, double left) =>
        new(top * PointsPerCm, right * PointsPerCm, bottom * PointsPerCm, left * PointsPerCm);
}
