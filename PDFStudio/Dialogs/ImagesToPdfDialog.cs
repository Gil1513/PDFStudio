using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Dialogs;

/// <summary>
/// Etapa 7.5: "Imagem para PDF". Lista as imagens escolhidas (dá para pôr em outra ordem, tirar e acrescentar),
/// o tamanho da página e a margem. <see cref="RunAsync"/> faz tudo: janela, onde salvar e a criação do PDF.
/// </summary>
public sealed partial class ImagesToPdfDialog : ContentDialog
{
    private readonly ObservableCollection<string> _images = [];
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBlock _count = new() { Opacity = 0.75 };
    private readonly ComboBox _size = new() { Header = "Tamanho da página", MinWidth = 200 };
    private readonly ComboBox _margin = new() { Header = "Margem", MinWidth = 140 };

    public IReadOnlyList<string> Images => _images;
    public ImagePageSize PageSize => (ImagePageSize)Math.Max(0, _size.SelectedIndex);
    public ImageMargin Margin => (ImageMargin)Math.Max(0, _margin.SelectedIndex);

    private ImagesToPdfDialog(IEnumerable<string> images)
    {
        Title = "Imagem para PDF";
        PrimaryButtonText = "Criar PDF…";
        CloseButtonText = "Cancelar";
        DefaultButton = ContentDialogButton.Primary;

        // Mesma ordem dos valores de ImagePageSize e ImageMargin.
        _size.Items.Add("Tamanho da imagem");
        _size.Items.Add("A4");
        _size.Items.Add("Carta");
        _size.SelectedIndex = 1;
        _margin.Items.Add("Sem margem");
        _margin.Items.Add("Pequena (1 cm)");
        _margin.Items.Add("Grande (2 cm)");
        _margin.SelectedIndex = 0;

        var add = new Button { Content = "Adicionar imagens…" };
        add.Click += async (_, _) =>
        {
            foreach (var file in await FilePickers.PickFilesAsync(ImagesToPdf.Extensions)) _images.Add(file);
        };

        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 460,
            Children =
            {
                new TextBlock { Text = "Cada imagem vira uma página, nesta ordem. Use as setas para mudar a ordem.", TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { Content = _list, MaxHeight = 300 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { add, _count } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _size, _margin } },
            },
        };

        _images.CollectionChanged += (_, _) => Fill();
        foreach (var image in images) _images.Add(image);
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        for (int i = 0; i < _images.Count; i++) _list.Children.Add(Row(i));
        _count.Text = _images.Count switch { 0 => "Nenhuma imagem", 1 => "1 imagem", var n => $"{n} imagens" };
        IsPrimaryButtonEnabled = _images.Count > 0;
    }

    /// <summary>Uma linha: miniatura, nome e os botões subir, descer e tirar.</summary>
    private Grid Row(int index)
    {
        string path = _images[index];
        string name = Path.GetFileName(path);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Miniatura pequena (o Windows decodifica já reduzida, sem carregar a foto inteira).
        var thumb = new Image { Width = 40, Height = 40, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        try { thumb.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 80 }; }
        catch (UriFormatException) { }
        var text = new TextBlock { Text = $"{index + 1}. {name}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(IconButton("", $"Subir {name}", index > 0, () => _images.Move(index, index - 1)));
        buttons.Children.Add(IconButton("", $"Descer {name}", index < _images.Count - 1, () => _images.Move(index, index + 1)));
        buttons.Children.Add(IconButton("", $"Tirar {name}", true, () => _images.RemoveAt(index)));
        Grid.SetColumn(buttons, 2);

        row.Children.Add(thumb);
        row.Children.Add(text);
        row.Children.Add(buttons);
        return row;
    }

    private static Button IconButton(string glyph, string name, bool enabled, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            IsEnabled = enabled,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// Fluxo completo: escolhe as imagens, mostra a janela, pergunta onde salvar e cria o PDF.
    /// Devolve o caminho do PDF criado (para abrir) ou null se o usuário desistiu ou deu erro (o erro é mostrado).
    /// </summary>
    public static async Task<string?> RunAsync(XamlRoot root)
    {
        var first = await FilePickers.PickFilesAsync(ImagesToPdf.Extensions);
        if (first.Count == 0) return null;
        var dialog = new ImagesToPdfDialog(first) { XamlRoot = root, RequestedTheme = App.Settings.Theme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Images.Count == 0) return null;

        var images = dialog.Images.ToList();
        var (size, margin) = (dialog.PageSize, dialog.Margin);
        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(images[0]));
        if (output is null) return null;

        // Janela de espera com o andamento (fotos grandes demoram um pouco).
        var bar = new ProgressBar { Maximum = images.Count, Value = 0, MinWidth = 320 };
        var status = new TextBlock { Text = $"0 de {images.Count} imagens" };
        var wait = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = App.Settings.Theme,
            Title = "Criando o PDF…",
            Content = new StackPanel { Spacing = 10, Children = { status, bar } },
        };
        _ = wait.ShowAsync();
        var progress = new Progress<int>(n => { bar.Value = n; status.Text = $"{n} de {images.Count} imagens"; });
        try
        {
            await Task.Run(() => ImagesToPdf.Create(images, size, margin, output, progress));
            return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            wait.Hide();
            await new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = App.Settings.Theme,
                Title = "Não foi possível criar o PDF",
                Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
            }.ShowAsync();
            return null;
        }
        finally
        {
            wait.Hide();
        }
    }
}
