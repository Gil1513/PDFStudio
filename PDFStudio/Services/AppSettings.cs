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
