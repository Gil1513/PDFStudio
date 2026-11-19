using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

/// <summary>Pedido para o visualizador: abrir <paramref name="Path"/> e já executar o comando <paramref name="Command"/>.</summary>
public sealed record ViewerRequest(string Path, string Command);

// Etapa 7.6: comprimir PDF. Grava um arquivo novo (o aberto não muda) e mostra quanto diminuiu.
public sealed partial class ViewerPage
{
    private async Task CompressAsync()
    {
        if (_path is null || DisplayPath is not { } source) return;
        long size = new FileInfo(source).Length;

        RadioButton Level(string title, string detail, bool isChecked) => new()
        {
            GroupName = "nivel",
            IsChecked = isChecked,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = detail, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
                },
            },
        };
        var light = Level("Leve", "Só limpeza. Textos e imagens ficam exatamente iguais.", false);
        var medium = Level("Médio (recomendado)", "Reduz as fotos grandes, com boa qualidade. Bom para enviar por e-mail.", true);
        var strong = Level("Forte", "Arquivo bem menor; as fotos perdem detalhe. Para textos, a leitura continua boa.", false);
        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"Tamanho atual: {DocumentPropertiesService.FormatSize(size)}. O PDF comprimido é salvo como um arquivo novo; este continua igual.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
                light, medium, strong,
            },
        };
        if (await IsSignedAsync(source))
            content.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
                Title = "Este PDF é assinado digitalmente",
                Message = "No arquivo comprimido a assinatura deixa de valer (o arquivo muda). O original continua assinado.",
            });

        var dialog = Prepare(new ContentDialog
        {
            Title = "Comprimir PDF",
            Content = content,
            PrimaryButtonText = "Comprimir…",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var level = light.IsChecked == true ? CompressLevel.Light : strong.IsChecked == true ? CompressLevel.Strong : CompressLevel.Medium;

        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_path) + "-comprimido");
        if (output is null) return;
        if (SamePath(output, _path))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escolha outro nome", "O arquivo comprimido não pode substituir o arquivo aberto.");
            return;
        }

        string? password = _password;
        LoadingBar.Visibility = Visibility.Visible;
        ShowStatus(InfoBarSeverity.Informational, "Comprimindo…", "Fotos grandes podem levar alguns segundos.");
        try
        {
            var result = await Task.Run(() => PdfCompressor.Compress(source, output, level, ImageShrinker.Shrink, password));
            string sizes = $"De {DocumentPropertiesService.FormatSize(result.Before)} para {DocumentPropertiesService.FormatSize(result.After)}";
            if (result.Saved < 0.01)
                ShowStatus(InfoBarSeverity.Informational, "Este PDF já estava compacto",
                    $"{sizes}. " + (level == CompressLevel.Light ? "Tente o nível Médio ou Forte, que reduz as fotos." : "Não havia fotos grandes para reduzir."),
                    "Abrir o arquivo novo", async () => await OpenAsync(output));
            else
                ShowStatus(InfoBarSeverity.Success, $"PDF comprimido: {result.Saved:P0} menor",
                    sizes + (result.ImagesReduced > 0 ? $" ({result.ImagesReduced} {(result.ImagesReduced == 1 ? "imagem reduzida" : "imagens reduzidas")})." : "."),
                    "Abrir o arquivo comprimido", async () => await OpenAsync(output));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível comprimir", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }
}
