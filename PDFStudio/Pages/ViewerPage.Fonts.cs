using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Fontes: mostra as fontes do documento aberto (e se o PDF Studio tem cada uma) e permite adicionar
// um arquivo de fonte que o usuário já tem ("Adicionar fonte…"), sem instalar no Windows.
public sealed partial class ViewerPage
{
    private async Task ShowFontsAsync()
    {
        var list = new StackPanel { Spacing = 10 };
        var message = new InfoBar { IsOpen = false, IsClosable = true };
        var add = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 14 }, new TextBlock { Text = "Adicionar fonte…" } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, "Adicionar fonte…");
        var folder = new HyperlinkButton { Content = "Abrir a pasta de fontes do PDF Studio" };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(FontLibrary.DownloadedFontsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FontLibrary.DownloadedFontsFolder}\"") { UseShellExecute = true });
        };

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 480,
            Children =
            {
                new TextBlock
                {
                    Text = "Para o texto editado ficar idêntico ao original, o PDF Studio precisa ter as mesmas fontes do documento. " +
                           "Se faltar alguma, adicione o arquivo dela (.ttf, .otf ou um .zip com fontes). A fonte fica só no PDF Studio, " +
                           "não é instalada no Windows. A licença da fonte é responsabilidade de quem a adiciona.",
                    TextWrapping = TextWrapping.Wrap,
                },
                message,
                new ScrollViewer { Content = list, MaxHeight = 340 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { add, folder } },
            },
        };

        async Task FillAsync()
        {
            list.Children.Clear();
            if (DisplayPath is not { } path)
            {
                list.Children.Add(new TextBlock { Text = "Abra um documento para ver as fontes dele.", Opacity = 0.7 });
                return;
            }
            list.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left });
            string? password = _password;
            List<DocumentFont> fonts;
            try
            {
                fonts = await Task.Run(() => PdfDocumentFonts.List(path, password));
            }
            catch (Exception ex)
            {
                list.Children.Clear();
                list.Children.Add(new TextBlock { Text = "Não foi possível ler as fontes: " + ex.Message, TextWrapping = TextWrapping.Wrap });
                return;
            }
            list.Children.Clear();
            if (fonts.Count == 0)
                list.Children.Add(new TextBlock { Text = "Este documento não tem texto (talvez seja só imagem).", Opacity = 0.7 });
            foreach (var font in fonts) list.Children.Add(FontRow(font));
        }

        add.Click += async (_, _) =>
        {
            string? file = await FilePickers.PickFileAsync(FontFiles.Extensions);
            if (file is null) return;
            add.IsEnabled = false;
            try
            {
                var result = await Task.Run(() => FontFiles.Add(file));
                message.Severity = result.Families.Count == 0 ? InfoBarSeverity.Warning
                    : result.Problems.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
                message.Title = result.Families.Count == 0 ? "Nenhuma fonte adicionada"
                    : "Fonte adicionada: " + string.Join(", ", result.Families);
                message.Message = string.Join("\n", result.Problems);
                message.IsOpen = true;
                await FillAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message.Severity = InfoBarSeverity.Error;
                message.Title = "Não foi possível adicionar a fonte";
                message.Message = ex.Message;
                message.IsOpen = true;
            }
            finally
            {
                add.IsEnabled = true;
            }
        };

        var dialog = Prepare(new ContentDialog
        {
            Title = "Fontes",
            Content = content,
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close,
        });
        dialog.Opened += async (_, _) => await FillAsync();
        await dialog.ShowAsync();
    }

    /// <summary>Uma linha: nome da fonte, detalhes e se o PDF Studio tem ela (✓) ou vai usar outra (!).</summary>
    private static FrameworkElement FontRow(DocumentFont font)
    {
        var style = new List<string>();
        if (font.Bold) style.Add("negrito");
        if (font.Italic) style.Add("itálico");
        string title = font.Family + (style.Count > 0 ? $" ({string.Join(", ", style)})" : "");
        string details = $"Nome no PDF: {font.PdfName} · {Plural(font.Letters, "letra", "letras")} · "
                         + (font.Embedded ? "embutida no PDF" : "não embutida no PDF");
        string status = font.Choice.Source switch
        {
            FontSource.Original => $"O PDF Studio tem esta fonte: o texto editado sai igual.",
            FontSource.Twin => $"Falta a fonte. Por enquanto usa {font.Choice.Use} (mesmas medidas, desenho um pouco diferente). " +
                               $"Adicione “{font.Family}” para ficar idêntico.",
            _ => $"Falta a fonte. Por enquanto usa {font.Choice.Use} (parecida). Adicione “{font.Family}” para ficar idêntico.",
        };
        var color = font.IsExact
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 120, 0));
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = font.IsExact ? "" : "", Foreground = color, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = details, Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = status, Foreground = color, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{title}: {status}");
        return row;
    }
}
