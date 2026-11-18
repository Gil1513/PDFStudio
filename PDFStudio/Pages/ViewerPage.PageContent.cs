using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Pages;

// Etapa 5: ferramentas que escrevem no conteúdo da página (não são comentários).
// 5.1 "Adicionar texto": clicar na página e digitar, com fonte do Windows, tamanho, cor, negrito e itálico.
public sealed partial class ViewerPage
{
    /// <summary>Margem interna (pontos) da caixa de digitação do "Adicionar texto"; o texto começa no ponto clicado.</summary>
    private const double PageTextPadding = 3;

    private bool _pageFontsLoaded;
    private bool _settingPageFont;

    /// <summary>Preenche a lista de fontes (na primeira vez) e mostra as últimas escolhas.</summary>
    private void InitPageFontOptions()
    {
        _settingPageFont = true;
        try
        {
            if (!_pageFontsLoaded)
            {
                _pageFontsLoaded = true;
                foreach (var name in SystemFonts.Installed()) PageFontBox.Items.Add(name);
                if (PageFontBox.Items.Count == 0) PageFontBox.Items.Add("Arial");
            }
            string font = App.Settings.PageTextFont;
            PageFontBox.SelectedItem = PageFontBox.Items.Contains(font) ? font
                : PageFontBox.Items.Contains("Arial") ? "Arial" : PageFontBox.Items[0];
            PageBoldToggle.IsChecked = App.Settings.PageTextBold;
            PageItalicToggle.IsChecked = App.Settings.PageTextItalic;
        }
        finally
        {
            _settingPageFont = false;
        }
    }

    private async void PageFontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingPageFont || PageFontBox.SelectedItem is not string font) return;
        // Algumas fontes não podem ser gravadas no PDF (ex.: fontes "PostScript" .otf): avisa logo na escolha.
        bool ok = await Task.Run(() => PdfPageContent.CanUseFont(font));
        if (!ok)
        {
            ShowStatus(InfoBarSeverity.Warning, "Fonte não disponível para PDF",
                $"A fonte \"{font}\" não pode ser gravada no PDF. Escolha outra.");
            InitPageFontOptions(); // volta para a última fonte que funcionou
            return;
        }
        App.Settings.PageTextFont = font;
        App.Settings.Save();
    }

    private void PageStyleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingPageFont) return;
        App.Settings.PageTextBold = PageBoldToggle.IsChecked == true;
        App.Settings.PageTextItalic = PageItalicToggle.IsChecked == true;
        App.Settings.Save();
    }

    private PageTextStyle CurrentPageTextStyle() => new(
        App.Settings.PageTextFont,
        App.Settings.PageTextFontSize,
        AnnotationColor.FromHex(App.Settings.PageTextColor),
        App.Settings.PageTextBold,
        App.Settings.PageTextItalic);
}
