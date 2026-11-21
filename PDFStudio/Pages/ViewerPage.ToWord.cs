using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 7.8: PDF → Word. Grava um .docx novo com o texto (mesma fonte, tamanho, cor, negrito e itálico) e as
// imagens, na ordem da página. O PDF aberto não muda.
public sealed partial class ViewerPage
{
    private async Task ConvertToWordAsync()
    {
        if (_path is null || DisplayPath is not { } source) return;
        if (!await CopyAllowedAsync("Conversão não permitida")) return;
        string? output = await FilePickers.SaveFileAsync(Path.GetFileNameWithoutExtension(_path) + ".docx");
        if (output is null) return;

        string? password = _password;
        LoadingBar.Visibility = Visibility.Visible;
        ShowStatus(InfoBarSeverity.Informational, "Convertendo para Word…", "Documentos grandes podem levar alguns segundos.");
        try
        {
            var result = await Task.Run(() => PdfToWord.Convert(source, output, password));
            string pages = $"{result.Pages} {(result.Pages == 1 ? "página" : "páginas")}";
            string message = result.Paragraphs == 0 && result.Images > 0
                ? $"{pages}, só com imagens: parece um documento escaneado. Para ter o texto editável no Word, faça o OCR antes (Ferramentas → OCR)."
                : $"{pages}, {result.Paragraphs} {(result.Paragraphs == 1 ? "parágrafo" : "parágrafos")} e {result.Images} {(result.Images == 1 ? "imagem" : "imagens")}. " +
                  "Tabelas e colunas lado a lado podem precisar de ajuste.";
            ShowStatus(InfoBarSeverity.Success, "Documento do Word criado", message, "Abrir", () => OpenWithDefaultApp(output));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível converter para Word",
                ex is IOException ? "O arquivo de destino está aberto em outro programa? Feche e tente de novo." : ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Abre o arquivo no programa que o Windows usa para esse tipo (ex.: o editor de texto instalado).</summary>
    private void OpenWithDefaultApp(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowStatus(InfoBarSeverity.Warning, "Não foi possível abrir o arquivo",
                $"Nenhum programa abriu arquivos .docx. O documento está salvo em: {path}");
        }
    }

    /// <summary>Mostra a aba Converter (vindo do cartão "Converter PDF" da tela inicial).</summary>
    private void ShowConverterTab()
    {
        if (Categories.FirstOrDefault(c => c.Key == "converter") is { } tab) CategoryList.SelectedItem = tab;
    }
}
