namespace PDFStudio.Core;

/// <summary>Matriz 2D do PDF [a b c d e f]; um ponto (x, y) vira (a·x + c·y + e, b·x + d·y + f).</summary>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>Primeiro esta, depois <paramref name="o"/> (como o PDF faz: "this × o").</summary>
    public PdfMatrix Then(PdfMatrix o) => new(A * o.A + B * o.C, A * o.B + B * o.D, C * o.A + D * o.C, C * o.B + D * o.D,
        E * o.A + F * o.C + o.E, E * o.B + F * o.D + o.F);

    public (double X, double Y) Origin => (E, F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>A matriz que desfaz esta (null se ela "achata" tudo e não tem volta).</summary>
    public PdfMatrix? Invert()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return null;
        return new(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }
}
