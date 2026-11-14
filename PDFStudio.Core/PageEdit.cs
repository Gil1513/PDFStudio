namespace PDFStudio.Core;

/// <summary>
/// Uma página no documento reorganizado: de qual página original ela vem (índice 0..n-1)
/// e quantos graus girar a mais (0, 90, 180, 270; sentido horário).
/// </summary>
public readonly record struct PageEdit(int SourceIndex, int Rotation = 0);
