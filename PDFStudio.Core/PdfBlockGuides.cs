namespace PDFStudio.Core;

/// <summary>Retângulo de um item na página (pontos, como na tela: y para baixo).</summary>
public readonly record struct GuideBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public GuideBox Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>Linha de alinhamento: vertical (em x = <see cref="At"/>, de <see cref="From"/> a <see cref="To"/> em y) ou horizontal.</summary>
public sealed record AlignGuide(bool Vertical, double At, double From, double To);

/// <summary>
/// Medida de um espaço vertical entre dois itens: em x = <see cref="X"/>, de <see cref="Top"/> a <see cref="Bottom"/>.
/// <see cref="Equal"/>: é igual ao espaço do item vizinho (de cima ou de baixo). <see cref="Reference"/>: é o espaço
/// copiado (entre os vizinhos), mostrado só quando fica igual.
/// </summary>
public sealed record GapGuide(double X, double Top, double Bottom, bool Equal, bool Reference = false)
{
    public double Size => Bottom - Top;
}

/// <summary>Quanto ajustar o arraste para "grudar" (pontos) e o que mostrar.</summary>
public sealed record GuideResult(double Dx, double Dy, IReadOnlyList<AlignGuide> Lines, IReadOnlyList<GapGuide> Gaps);

/// <summary>
/// Guias ao arrastar itens (como nos editores de design): alinhamento das bordas e do centro com os outros itens e
/// espaço igual ao dos vizinhos de cima/baixo. Perto (até <c>tolerance</c>), o item gruda na posição.
/// </summary>
public static class PdfBlockGuides
{
    private const double Same = 0.5;

    private static bool OverlapX(GuideBox a, GuideBox b) => a.X < b.Right && a.Right > b.X;

    /// <summary>Item mais perto acima de <paramref name="box"/> (que fica por cima dele, na mesma coluna).</summary>
    private static GuideBox? Above(GuideBox box, IReadOnlyList<GuideBox> others, double slack) =>
        others.Where(o => OverlapX(o, box) && o.Bottom <= box.Y + slack && o.CenterY < box.CenterY)
            .OrderByDescending(o => o.Bottom).Cast<GuideBox?>().FirstOrDefault();

    private static GuideBox? Below(GuideBox box, IReadOnlyList<GuideBox> others, double slack) =>
        others.Where(o => OverlapX(o, box) && o.Y >= box.Bottom - slack && o.CenterY > box.CenterY)
            .OrderBy(o => o.Y).Cast<GuideBox?>().FirstOrDefault();

    /// <param name="moving">Onde o item (ou o conjunto arrastado) está agora.</param>
    /// <param name="others">Os outros itens da página.</param>
    /// <param name="tolerance">Distância (pontos) em que o item gruda; 0 = não gruda, só mostra o que já está igual.</param>
    public static GuideResult Snap(GuideBox moving, IReadOnlyList<GuideBox> others, double tolerance)
    {
        // 1) Horizontal: borda esquerda, centro ou direita igual à de outro item.
        double dx = Best(others.SelectMany(o => Pairs(moving.X, moving.CenterX, moving.Right, o.X, o.CenterX, o.Right)), tolerance);
        var m = moving.Offset(dx, 0);

        // 2) Vertical: topo/meio/base iguais (itens lado a lado) ou o mesmo espaço dos vizinhos.
        var dys = new List<double>(others.SelectMany(o => Pairs(m.Y, m.CenterY, m.Bottom, o.Y, o.CenterY, o.Bottom)));
        var rest = others;
        if (Above(m, rest, tolerance) is { } a)
        {
            if (Above(a, rest, 0) is { } aa) dys.Add(a.Bottom + (a.Y - aa.Bottom) - m.Y);
            if (Below(m, rest, tolerance) is { } b0)
                dys.Add(a.Bottom + (b0.Y - a.Bottom - m.Height) / 2 - m.Y); // no meio, com espaços iguais
        }
        if (Below(m, rest, tolerance) is { } b && Below(b, rest, 0) is { } bb)
            dys.Add(b.Y - (bb.Y - b.Bottom) - m.Bottom);
        double dy = Best(dys, tolerance);
        m = m.Offset(0, dy);

        return new GuideResult(dx, dy, Lines(m, others), Gaps(m, others));
    }

    private static IEnumerable<double> Pairs(double a1, double a2, double a3, double b1, double b2, double b3)
    {
        foreach (double a in new[] { a1, a2, a3 })
            foreach (double b in new[] { b1, b2, b3 })
                yield return b - a;
    }

    private static double Best(IEnumerable<double> candidates, double tolerance)
    {
        double best = 0, bestAbs = double.MaxValue;
        foreach (double d in candidates)
            if (Math.Abs(d) <= tolerance && Math.Abs(d) < bestAbs) { best = d; bestAbs = Math.Abs(d); }
        return best;
    }

    /// <summary>Linhas de alinhamento que valem na posição final.</summary>
    private static List<AlignGuide> Lines(GuideBox m, IReadOnlyList<GuideBox> others)
    {
        var lines = new List<AlignGuide>();
        foreach (var (mv, pick) in new (double, Func<GuideBox, double>)[] { (m.X, o => o.X), (m.CenterX, o => o.CenterX), (m.Right, o => o.Right) })
        {
            var hits = others.Where(o => Math.Abs(pick(o) - mv) < Same).ToList();
            if (hits.Count > 0)
                lines.Add(new AlignGuide(true, mv, Math.Min(m.Y, hits.Min(o => o.Y)), Math.Max(m.Bottom, hits.Max(o => o.Bottom))));
        }
        foreach (var (mv, pick) in new (double, Func<GuideBox, double>)[] { (m.Y, o => o.Y), (m.CenterY, o => o.CenterY), (m.Bottom, o => o.Bottom) })
        {
            // Horizontais só para itens lado a lado (nos empilhados, quem mostra é a medida do espaço).
            var hits = others.Where(o => !OverlapX(o, m) && Math.Abs(pick(o) - mv) < Same).ToList();
            if (hits.Count > 0)
                lines.Add(new AlignGuide(false, mv, Math.Min(m.X, hits.Min(o => o.X)), Math.Max(m.Right, hits.Max(o => o.Right))));
        }
        return lines;
    }

    /// <summary>Medidas dos espaços até o vizinho de cima e o de baixo; iguais ao espaço vizinho ficam marcadas.</summary>
    private static List<GapGuide> Gaps(GuideBox m, IReadOnlyList<GuideBox> others)
    {
        var gaps = new List<GapGuide>();
        static double MidX(GuideBox a, GuideBox b) => (Math.Max(a.X, b.X) + Math.Min(a.Right, b.Right)) / 2;
        var a = Above(m, others, 0);
        var b = Below(m, others, 0);
        double? up = a is { } ga ? m.Y - ga.Bottom : null, down = b is { } gb ? gb.Y - m.Bottom : null;
        bool middle = up is > 0 && down is > 0 && Math.Abs(up.Value - down.Value) < Same;
        if (a is { } above && up > 0)
        {
            var aa = Above(above, others, 0);
            bool equal = aa is { } x && Math.Abs((above.Y - x.Bottom) - up.Value) < Same;
            gaps.Add(new GapGuide(MidX(m, above), above.Bottom, m.Y, equal || middle));
            if (equal) gaps.Add(new GapGuide(MidX(above, aa!.Value), aa.Value.Bottom, above.Y, true, Reference: true));
        }
        if (b is { } below && down > 0)
        {
            var bb = Below(below, others, 0);
            bool equal = bb is { } x && Math.Abs((x.Y - below.Bottom) - down.Value) < Same;
            gaps.Add(new GapGuide(MidX(m, below), m.Bottom, below.Y, equal || middle));
            if (equal) gaps.Add(new GapGuide(MidX(below, bb!.Value), below.Bottom, bb.Value.Y, true, Reference: true));
        }
        return gaps;
    }
}
