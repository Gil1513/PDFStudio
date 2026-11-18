using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

/// <summary>
/// Converte posições entre a página "como aparece na tela" e o sistema de coordenadas do PDF.
/// <para>
/// Tela ("view"): pontos (1/72 pol.) a partir do canto superior esquerdo da página visível, y para baixo,
/// já considerando o recorte (/CropBox) e a rotação (/Rotate).<br/>
/// PDF: pontos no espaço da página sem rotação, origem embaixo à esquerda, y para cima.
/// </para>
/// </summary>
public sealed class PageGeometry
{
    private readonly double _x1, _y1, _w, _h;

    public PageGeometry(double x1, double y1, double x2, double y2, int rotation)
    {
        _x1 = Math.Min(x1, x2);
        _y1 = Math.Min(y1, y2);
        _w = Math.Abs(x2 - x1);
        _h = Math.Abs(y2 - y1);
        Rotation = PdfOperations.Normalize(rotation);
    }

    /// <summary>Geometria da área visível da página (recorte, ou a página inteira).</summary>
    public static PageGeometry Of(PdfPage page)
    {
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        return new PageGeometry(box.X1, box.Y1, box.X2, box.Y2, page.Rotate);
    }

    /// <summary>0, 90, 180 ou 270 (sentido horário).</summary>
    public int Rotation { get; }

    private bool Turned => Rotation is 90 or 270;
    public double ViewWidth => Turned ? _h : _w;
    public double ViewHeight => Turned ? _w : _h;

    /// <summary>Ponto da tela → ponto do PDF.</summary>
    public XPoint ViewToPdf(double vx, double vy)
    {
        // (u, v) = posição na página sem rotação, a partir do canto superior esquerdo.
        var (u, v) = Rotation switch
        {
            90 => (vy, _h - vx),
            180 => (_w - vx, _h - vy),
            270 => (_w - vy, vx),
            _ => (vx, vy),
        };
        return new XPoint(_x1 + u, _y1 + _h - v);
    }

    /// <summary>Ponto do PDF → ponto da tela.</summary>
    public XPoint PdfToView(double px, double py)
    {
        double u = px - _x1, v = _y1 + _h - py;
        return Rotation switch
        {
            90 => new XPoint(_h - v, u),
            180 => new XPoint(_w - u, _h - v),
            270 => new XPoint(v, _w - u),
            _ => new XPoint(u, v),
        };
    }

    /// <summary>Retângulo da tela (x, y, largura, altura) → retângulo do PDF (normalizado).</summary>
    public PdfRectangle ViewRectToPdf(double x, double y, double width, double height)
    {
        var a = ViewToPdf(x, y);
        var b = ViewToPdf(x + width, y + height);
        return new PdfRectangle(new XPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new XPoint(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }

    /// <summary>Retângulo do PDF → retângulo da tela (x, y, largura, altura).</summary>
    public (double X, double Y, double Width, double Height) PdfRectToView(double x1, double y1, double x2, double y2)
    {
        var a = PdfToView(x1, y1);
        var b = PdfToView(x2, y2);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }
}
