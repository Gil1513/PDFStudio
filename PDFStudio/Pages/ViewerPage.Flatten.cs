using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Pages;

/// <summary>Etapa 6.3: achatar comentários e campos de formulário (viram parte da página).</summary>
public sealed partial class ViewerPage
{
    private async Task FlattenAsync()
    {
        if (DisplayPath is not { } source) return;
        int comments, fields;
        try
        {
            (comments, fields) = await Task.Run(() => PdfFlatten.Count(source));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível ler o PDF", ex.Message);
            return;
        }
        if (comments == 0 && fields == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Nada para achatar", "Este PDF não tem comentários nem campos de formulário (links e assinaturas não contam).");
            return;
        }

        var commentsBox = new CheckBox { Content = $"Comentários ({comments})", IsChecked = comments > 0, IsEnabled = comments > 0 };
        var fieldsBox = new CheckBox { Content = $"Campos de formulário ({fields})", IsChecked = fields > 0, IsEnabled = fields > 0 };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Achatar",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "O que for achatado passa a ser parte do desenho da página, do jeito que aparece agora: " +
                               "não pode mais ser editado, movido ou apagado, e campos deixam de ser preenchíveis. " +
                               "Links continuam funcionando e campos de assinatura digital não são alterados.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 440,
                    },
                    commentsBox, fieldsBox,
                    new TextBlock
                    {
                        Text = "Até salvar, Desfazer (Ctrl+Z) volta atrás.",
                        Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 440,
                    },
                },
            },
            PrimaryButtonText = "Achatar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        void Update(object? s, RoutedEventArgs e) => dialog.IsPrimaryButtonEnabled = commentsBox.IsChecked == true || fieldsBox.IsChecked == true;
        commentsBox.Click += Update;
        fieldsBox.Click += Update;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        bool doComments = commentsBox.IsChecked == true, doFields = fieldsBox.IsChecked == true;
        FlattenResult? result = null;
        if (!await ApplyEditAsync((input, output) => result = PdfFlatten.Apply(input, output, doComments, doFields), label: "Achatar")) return;
        if (result is null) return;

        var parts = new List<string>();
        if (result.Comments > 0) parts.Add(Plural(result.Comments, "comentário", "comentários"));
        if (result.Fields > 0) parts.Add(Plural(result.Fields, "campo", "campos"));
        string summary = $"{string.Join(" e ", parts)} agora fazem parte da página.";
        if (result.WithoutAppearance > 0)
            ShowStatus(InfoBarSeverity.Warning, "Achatado, com ressalva",
                $"{summary} {Plural(result.WithoutAppearance, "item não tinha", "itens não tinham")} desenho guardado no PDF (ou estava oculto) e saiu sem aparecer. Ctrl+Z desfaz.");
        else
            ShowStatus(InfoBarSeverity.Success, "Achatado", $"{summary} Salve com Ctrl+S; Ctrl+Z desfaz.");
    }
}
