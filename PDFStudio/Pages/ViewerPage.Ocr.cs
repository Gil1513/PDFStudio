using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Media.Ocr;
using OcrWord = PDFStudio.Core.OcrWord;

namespace PDFStudio.Pages;

// Etapa 7.7: OCR. Reconhece o texto das páginas escaneadas (OCR do Windows, no próprio computador) e grava como
// texto invisível por cima da imagem: a página fica igual, mas dá para pesquisar, selecionar e copiar.
// É uma alteração como as outras: Desfazer volta atrás e Salvar grava no arquivo.
public sealed partial class ViewerPage
{
    private CancellationTokenSource? _ocrCancel;

    private async Task OcrAsync()
    {
        if (_document is null || DisplayPath is not { } source) return;
        if (_ocrCancel is not null) return; // já está reconhecendo
        var languages = OcrService.Languages;
        if (languages.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "O Windows não tem o reconhecimento de texto instalado",
                "Em Configurações → Hora e idioma → Idioma e região, abra as opções de Português (Brasil) e instale " +
                "\"Reconhecimento óptico de caracteres\". Depois tente de novo.");
            return;
        }

        List<int> withoutText;
        try { withoutText = await Task.Run(() => PdfOcr.PagesWithoutText(source)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { withoutText = Enumerable.Range(0, _slots.Count).ToList(); }
        int pageCount = _slots.Count, current = CurrentIndex;

        var language = new ComboBox { Header = "Idioma do texto", MinWidth = 260 };
        foreach (var l in languages) language.Items.Add(l.DisplayName);
        // Compara pelo código ("pt-BR"): cada consulta ao Windows devolve objetos novos.
        string? preferred = OcrService.DefaultLanguage?.LanguageTag;
        language.SelectedIndex = Math.Max(0, languages.ToList().FindIndex(l => l.LanguageTag == preferred));
        var onlyScanned = new RadioButton { GroupName = "ocr", Content = $"Só as páginas sem texto ({withoutText.Count})", IsChecked = withoutText.Count > 0, IsEnabled = withoutText.Count > 0 };
        var all = new RadioButton { GroupName = "ocr", Content = $"Todas as páginas ({pageCount})", IsChecked = withoutText.Count == 0 };
        var page = new RadioButton { GroupName = "ocr", Content = $"Só a página atual ({current + 1})" };
        var content = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = "O OCR lê o texto das páginas escaneadas (fotos de documentos). A página continua igual, mas o texto " +
                           "passa a poder ser pesquisado, selecionado e copiado. O reconhecimento é feito no seu computador; " +
                           "nada é enviado para a internet.",
                    TextWrapping = TextWrapping.Wrap,
                },
                language, onlyScanned, all, page,
            },
        };
        if (withoutText.Count == 0)
            content.Children.Add(new TextBlock
            {
                Text = "Todas as páginas já têm texto. Fazer o OCR nelas pode duplicar o texto na pesquisa.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
            });

        var dialog = Prepare(new ContentDialog
        {
            Title = "Reconhecer texto (OCR)",
            Content = content,
            PrimaryButtonText = "Reconhecer",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        List<int> pages = onlyScanned.IsChecked == true ? withoutText
            : page.IsChecked == true ? [current]
            : Enumerable.Range(0, pageCount).ToList();
        var engine = OcrEngine.TryCreateFromLanguage(languages[Math.Max(0, language.SelectedIndex)]);
        if (engine is null)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível usar o OCR", "O Windows não abriu o reconhecimento para esse idioma.");
            return;
        }

        var cancel = _ocrCancel = new CancellationTokenSource();
        var doc = _document;
        var found = new Dictionary<int, IReadOnlyList<OcrWord>>();
        int words = 0;
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            for (int i = 0; i < pages.Count && !cancel.IsCancellationRequested; i++)
            {
                ShowStatus(InfoBarSeverity.Informational, "Reconhecendo o texto…", $"Página {pages[i] + 1} ({i + 1} de {pages.Count})",
                    "Parar", () => cancel.Cancel());
                var list = await OcrService.RecognizeAsync(doc, pages[i], engine);
                if (list.Count > 0) found[pages[i]] = list;
                words += list.Count;
            }
            if (doc != _document) return; // outro arquivo foi aberto no meio
            if (words == 0)
            {
                ShowStatus(InfoBarSeverity.Informational, "Nenhum texto encontrado",
                    "O OCR não achou palavras nessas páginas. Confira o idioma, ou se a imagem está nítida e de pé.");
                return;
            }
            bool stopped = cancel.IsCancellationRequested;
            if (await ApplyEditAsync((input, output) => PdfOcr.AddTextLayer(input, found, output), label: "OCR"))
                ShowStatus(InfoBarSeverity.Success,
                    stopped ? $"OCR parado: texto gravado em {found.Count} de {pages.Count} páginas" : "Texto reconhecido",
                    $"{words} palavras em {found.Count} {(found.Count == 1 ? "página" : "páginas")}. Agora dá para pesquisar (Ctrl+F), " +
                    "selecionar e copiar. Salve para manter no arquivo.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível reconhecer o texto", ex.Message);
        }
        finally
        {
            _ocrCancel = null;
            cancel.Dispose();
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }
}
