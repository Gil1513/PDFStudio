using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PDFStudio.Pages;

// Etapa 7.4: PDF→imagem. Cada página escolhida vira um arquivo PNG ou JPG numa pasta, desenhada pelo motor
// de PDF do Windows (o mesmo da tela). As contas e os nomes ficam em PageImages (Core, com testes).
public sealed partial class ViewerPage
{
    private async Task ExportImagesAsync()
    {
        if (_document is null || _path is null) return;
        int pageCount = _slots.Count;
        int current = Math.Clamp((int)PageNumberBox.Value, 1, pageCount);

        var png = new RadioButton { Content = "PNG (sem perda, melhor para texto)", IsChecked = true, GroupName = "formato" };
        var jpg = new RadioButton { Content = "JPG (arquivo menor, melhor para fotos)", GroupName = "formato" };
        var resolution = new ComboBox { Header = "Qualidade", MinWidth = 260 };
        resolution.Items.Add("Tela (96 dpi)");
        resolution.Items.Add("Padrão (150 dpi)");
        resolution.Items.Add("Impressão (300 dpi)");
        resolution.SelectedIndex = 1;
        var all = new RadioButton { Content = $"Todas ({pageCount})", IsChecked = true, GroupName = "paginas" };
        var only = new RadioButton { Content = $"Só a página atual ({current})", GroupName = "paginas" };
        var some = new RadioButton { Content = "Escolher:", GroupName = "paginas" };
        var ranges = new TextBox { PlaceholderText = "ex.: 1-3, 5", Width = 160, IsEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ranges, "Páginas para converter");
        some.Checked += (_, _) => { ranges.IsEnabled = true; ranges.Focus(FocusState.Programmatic); };
        some.Unchecked += (_, _) => ranges.IsEnabled = false;
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 420,
            Children =
            {
                new TextBlock { Text = "Formato", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                png, jpg,
                resolution,
                new TextBlock { Text = "Páginas", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 6, 0, 0) },
                all, only,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { some, ranges } },
                error,
                new TextBlock { Text = "Depois você escolhe a pasta onde as imagens serão salvas. Nenhum arquivo existente é substituído.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            },
        };

        List<int> pages = [];
        var dialog = Prepare(new ContentDialog
        {
            Title = "Converter para imagem",
            Content = content,
            PrimaryButtonText = "Escolher pasta…",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                pages = all.IsChecked == true ? PageImages.Pages(null, pageCount)
                    : only.IsChecked == true ? [current - 1]
                    : PageImages.Pages(ranges.Text, pageCount);
            }
            catch (FormatException ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var format = jpg.IsChecked == true ? PageImageFormat.Jpeg : PageImageFormat.Png;
        int dpi = PageImages.Resolutions[Math.Max(0, resolution.SelectedIndex)];
        string? folder = await FilePickers.PickFolderAsync();
        if (folder is null) return;

        var doc = _document;
        string name = Path.GetFileName(_path);
        var saved = new List<string>();
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            foreach (int index in pages)
            {
                ShowStatus(InfoBarSeverity.Informational, "Convertendo para imagem…", $"Página {index + 1} ({saved.Count + 1} de {pages.Count})");
                string path = PageImages.FreePath(folder, PageImages.FileName(name, index + 1, pageCount, format));
                await PdfRenderer.SavePageImageAsync(doc, index, dpi, format, path);
                saved.Add(path);
            }
            string first = saved[0];
            ShowStatus(InfoBarSeverity.Success,
                saved.Count == 1 ? "Imagem salva" : $"{saved.Count} imagens salvas",
                saved.Count == 1 ? Path.GetFileName(first) : $"Na pasta {folder}",
                "Mostrar na pasta",
                () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar as imagens",
                (saved.Count > 0 ? $"{saved.Count} foram salvas antes do erro. " : "") + ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }
}
