using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDFStudio.Core;
using Windows.UI;

namespace PDFStudio.Services;

/// <summary>Um cartão de ferramenta da tela inicial.</summary>
public sealed class ToolInfo(string key, string title, string description, string glyph, Color color, bool available)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;
    public SolidColorBrush IconBrush { get; } = new(color);
    public bool IsAvailable { get; } = available;
    public Visibility SoonVisibility => IsAvailable ? Visibility.Collapsed : Visibility.Visible;
    public double ContentOpacity => IsAvailable ? 1.0 : 0.55;
    public string ToolTip => IsAvailable ? Description : $"{Title}: em breve";
}

/// <summary>Grupo de cartões (as "Ferramentas rápidas" são um grupo sem título).</summary>
public sealed class ToolGroup(string name, IReadOnlyList<ToolInfo> tools)
{
    public string Name { get; } = name;
    public IReadOnlyList<ToolInfo> Tools { get; } = tools;
    public Visibility HeaderVisibility => string.IsNullOrEmpty(Name) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Catálogo das ferramentas. As marcadas como indisponíveis chegam nas próximas etapas.</summary>
public static class ToolCatalog
{
    private static Color C(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // Cores próprias do PDF Studio.
    private static readonly Color Blue = C(0x3D7BFA), Orange = C(0xF2883A), Teal = C(0x1FB5A3), Green = C(0x3DB06B),
        Purple = C(0x8A63F0), Red = C(0xE5534B), Pink = C(0xD94F9C), Amber = C(0xE0A31B), Slate = C(0x5B6B8C), Cyan = C(0x2BA7D9);

    public static readonly ToolInfo Viewer = new("viewer", "Abrir e visualizar", "Leia o PDF com zoom e navegação por páginas.", "", Blue, true);
    public static readonly ToolInfo Edit = new("editar", "Editar PDF", "Edite textos e imagens no arquivo.", "", Orange, false);
    public static readonly ToolInfo Convert = new("converter", "Converter PDF", "Converta PDFs para Word, imagens e mais.", "", Teal, false);
    public static readonly ToolInfo Ocr = new("ocr", "PDF por OCR", "Deixe PDFs digitalizados pesquisáveis e copiáveis.", "", Green, false);
    public static readonly ToolInfo Merge = new("combinar", "Combinar PDFs", "Junte vários arquivos em um único PDF.", "", Blue, true);
    public static readonly ToolInfo Split = new("dividir", "Dividir PDF", "Separe as páginas em vários arquivos.", "", Cyan, true);
    public static readonly ToolInfo Organize = new("organizar", "Organizar páginas", "Gire e reordene as páginas do documento.", "", Purple, true);
    public static readonly ToolInfo Sign = new("assinar", "Assinar PDF", "Assinatura digital com certificado .pfx.", "", Pink, true);
    public static readonly ToolInfo Compress = new("comprimir", "Comprimir PDF", "Reduza o tamanho do arquivo.", "", Amber, false);
    public static readonly ToolInfo Form = new("formulario", "Preencher formulário", "Preencha campos e marque opções.", "", Green, false);
    public static readonly ToolInfo Batch = new("lote", "PDFs em lote", "Aplique uma ação em vários arquivos de uma vez.", "", Slate, false);

    /// <summary>Cartões da seção "Ferramentas rápidas".</summary>
    public static IReadOnlyList<ToolGroup> Quick { get; } =
    [
        new("", [Merge, Split, Organize, Sign, Viewer, Edit, Convert, Ocr, Compress, Batch]),
    ];

    /// <summary>Todas as ferramentas, por categoria.</summary>
    public static IReadOnlyList<ToolGroup> All { get; } =
    [
        new("Visualizar e organizar", [Viewer, Merge, Split, Organize,
            new("paginas", "Inserir e excluir páginas", "Extraia, exclua, insira e recorte páginas.", "", Purple, false)]),
        new("Editar e comentar", [Edit,
            new("comentar", "Comentários", "Realce, sublinhe, desenhe e adicione notas.", "", Orange, false),
            new("marcadagua", "Marca d'água", "Adicione texto ou imagem por trás do conteúdo.", "", Teal, false),
            new("cabecalho", "Cabeçalho e rodapé", "Números de página, datas e textos fixos.", "", Slate, false)]),
        new("Converter", [Convert, Ocr,
            new("imagens", "PDF para imagem", "Salve cada página como PNG ou JPG.", "", Cyan, false),
            new("criarimagem", "Imagem para PDF", "Junte fotos e digitalizações em um PDF.", "", Green, false)]),
        new("Proteger e assinar", [Sign,
            new("senha", "Proteger com senha", "Coloque ou remova a senha do PDF.", "", Red, false),
            new("redigir", "Redigir", "Cubra informações sensíveis.", "", Slate, false),
            new("achatar", "Achatar", "Fixe comentários e campos no conteúdo.", "", Amber, false)]),
        new("Otimizar e automatizar", [Compress, Form, Batch]),
    ];
}

/// <summary>Uma linha da lista de arquivos recentes (ou uma pasta, no modo "Pastas recentes").</summary>
public sealed partial class RecentItem : INotifyPropertyChanged
{
    private bool _starred;
    private BitmapImage? _thumbnail;

    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsFolder { get; init; }
    public bool Exists { get; init; }
    public DateTime Modified { get; init; }
    public long Size { get; init; }
    public DateTime LastOpened { get; init; }
    public int FileCount { get; init; }

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string ModifiedText => !Exists ? (IsFolder ? "Pasta não encontrada" : "Arquivo não encontrado") : FriendlyDate(Modified);
    public string SizeText => IsFolder ? $"{FileCount} {(FileCount == 1 ? "arquivo" : "arquivos")}" : Exists ? DocumentPropertiesService.FormatSize(Size) : "—";
    public string Glyph => IsFolder ? "" : "";
    public double RowOpacity => Exists ? 1.0 : 0.5;
    public Visibility StarVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ThumbnailPlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    internal bool ThumbnailRequested { get; set; }

    public bool Starred
    {
        get => _starred;
        set
        {
            if (_starred == value) return;
            _starred = value;
            OnChanged(nameof(Starred));
            OnChanged(nameof(StarGlyph));
            OnChanged(nameof(StarToolTip));
        }
    }

    public string StarGlyph => _starred ? "" : "";
    public string StarToolTip => _starred ? "Remover estrela" : "Marcar com estrela";

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnChanged(nameof(Thumbnail));
            OnChanged(nameof(ThumbnailPlaceholderVisibility));
        }
    }

    public static string FriendlyDate(DateTime d)
    {
        var today = DateTime.Today;
        if (d.Date == today) return $"Hoje, {d:HH:mm}";
        if (d.Date == today.AddDays(-1)) return $"Ontem, {d:HH:mm}";
        return d.Year == today.Year ? d.ToString("dd 'de' MMM, HH:mm", new System.Globalization.CultureInfo("pt-BR"))
            : d.ToString("dd/MM/yyyy");
    }

    public static RecentItem FromEntry(RecentFileEntry e)
    {
        var info = new FileInfo(e.Path);
        bool exists = info.Exists;
        return new RecentItem
        {
            Path = e.Path,
            Name = System.IO.Path.GetFileName(e.Path),
            Exists = exists,
            Modified = exists ? info.LastWriteTime : default,
            Size = exists ? info.Length : 0,
            LastOpened = e.LastOpened,
            Starred = e.Starred,
        };
    }

    public static RecentItem FromFolder(RecentFolder f)
    {
        bool exists = Directory.Exists(f.Path);
        return new RecentItem
        {
            Path = f.Path,
            Name = System.IO.Path.GetFileName(f.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : f.Path,
            IsFolder = true,
            Exists = exists,
            Modified = f.LastOpened,
            LastOpened = f.LastOpened,
            FileCount = f.FileCount,
        };
    }

    /// <summary>Nome lido pelo narrador de tela.</summary>
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
