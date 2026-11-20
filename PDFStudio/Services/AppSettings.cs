using System.Text.Json;
using Microsoft.UI.Xaml;

namespace PDFStudio.Services;

/// <summary>Preferências do app, gravadas em %LOCALAPPDATA%\PDFStudio\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDFStudio", "settings.json");

    /// <summary>Dark = escuro (padrão), Light = claro, Default = igual ao Windows.</summary>
    public ElementTheme Theme { get; set; } = ElementTheme.Dark;

    /// <summary>true = arquivos recentes em grade; false = lista.</summary>
    public bool RecentAsGrid { get; set; }

    /// <summary>Nome gravado como autor dos comentários (padrão: usuário do Windows).</summary>
    public string CommentAuthor { get; set; } = Environment.UserName;

    /// <summary>Última cor e espessura usadas nas ferramentas de comentário.</summary>
    public string CommentColor { get; set; } = "#E53935";
    public double CommentLineWidth { get; set; } = 2;

    /// <summary>Cor do realce (separada das outras ferramentas: o padrão é o amarelo de marca-texto).</summary>
    public string HighlightColor { get; set; } = "#FDD835";

    /// <summary>Carimbo: último modelo escolhido, texto do personalizado e se inclui data e autor.</summary>
    public int StampPreset { get; set; }
    public string StampCustomText { get; set; } = "";
    public bool StampWithDetail { get; set; }

    /// <summary>Último tamanho de letra usado na caixa de texto, em pontos.</summary>
    public double CommentFontSize { get; set; } = 12;

    /// <summary>Etapa 5.1 — "Adicionar texto" (escrito na própria página): últimas escolhas de fonte, tamanho, cor e estilo.</summary>
    public string PageTextFont { get; set; } = "Arial";
    public double PageTextFontSize { get; set; } = 14;
    public string PageTextColor { get; set; } = "#212121";
    public bool PageTextBold { get; set; }
    public bool PageTextItalic { get; set; }
    public string WatermarkFont { get; set; } = "Arial";
    public string HeaderFooterFont { get; set; } = "Arial";
    /// <summary>Etapa 8.2 — assinar em lote: o último certificado .pfx usado (só o caminho; a senha nunca é guardada).</summary>
    public string? LastPfxPath { get; set; }
    /// <summary>Etapa 8.4 — o último prefixo da numeração Bates e o último modelo de "Renomear".</summary>
    public string? BatesPrefix { get; set; }
    public string? RenameTemplate { get; set; }
    /// <summary>Tela de impressão própria: a última impressora usada (volta escolhida na próxima vez).</summary>
    public string? LastPrinter { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Arquivo inválido: usa o padrão.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Não conseguir salvar preferências não deve travar o app.
        }
    }
}
