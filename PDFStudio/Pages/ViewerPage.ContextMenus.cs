using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Foundation;

namespace PDFStudio.Pages;

// Botão direito na página (sem ferramenta ligada):
//  - em cima de palavras (ou do texto selecionado): copiar, realçar, sublinhar, riscar, pesquisar…
//  - no resto da página: girar, mover para cima/baixo, duplicar, inserir em branco, extrair, apagar.
// Tudo vira uma alteração como as outras: dá para desfazer (Ctrl+Z) e só grava no arquivo ao salvar.
public sealed partial class ViewerPage
{
    private async Task ShowContextMenuAsync(DrawingSurface surface, PageSlot slot, Point p)
    {
        if (IsOnCopySelection(surface, p)) { ShowTextMenu(surface, slot, p); return; }

        if (DisplayPath is not null)
        {
            List<TextWord> words;
            try { words = await WordsOfPage(slot.Index); } catch (Exception) { words = []; }
            double x = p.X / DipsPerPoint, y = p.Y / DipsPerPoint;
            if (words.Where(w => w.DistanceTo(x, y) < 1).OrderBy(w => w.DistanceTo(x, y)).FirstOrDefault() is { } word)
            {
                SetCopySelection(surface, slot, [word]); // clicou numa palavra: ela fica selecionada
                ShowTextMenu(surface, slot, p);
                return;
            }
        }
        ClearCopySelection();
        ShowPageMenu(surface, slot, p);
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action click, bool enabled = true, string? keys = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        if (keys is not null) item.KeyboardAcceleratorTextOverride = keys;
        item.Click += (_, _) => click();
        return item;
    }

    // ---------------- Texto ----------------

    private void ShowTextMenu(DrawingSurface surface, PageSlot slot, Point p)
    {
        var selected = _copySelected;
        string text = PdfTextLayout.TextOf(selected).Trim();
        string shortText = text.Length > 24 ? text[..22].TrimEnd() + "…" : text;

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Copiar", "", CopySelectedText, keys: "Ctrl+C"));
        menu.Items.Add(MenuItem("Selecionar todo o texto da página", "", () => _ = SelectAllTextAsync(surface, slot)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Realçar", "", () => _ = MarkSelectionAsync(slot, AnnotationKind.Highlight)));
        menu.Items.Add(MenuItem("Sublinhar", "", () => _ = MarkSelectionAsync(slot, AnnotationKind.Underline)));
        menu.Items.Add(MenuItem("Riscar", "", () => _ = MarkSelectionAsync(slot, AnnotationKind.StrikeOut)));
        if (text.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem($"Pesquisar \"{shortText}\" no documento", "", () => SearchFor(text)));
        }
        menu.ShowAt(surface, new FlyoutShowOptions { Position = p });
    }

    private async Task SelectAllTextAsync(DrawingSurface surface, PageSlot slot)
    {
        var words = await WordsOfPage(slot.Index);
        if (words.Count == 0) return;
        SetCopySelection(surface, slot, words);
    }

    /// <summary>Realça, sublinha ou risca o texto selecionado (com as cores das ferramentas de comentário).</summary>
    private async Task MarkSelectionAsync(PageSlot slot, AnnotationKind kind)
    {
        var selected = _copySelected;
        if (selected.Count == 0) return;
        var lines = PdfTextLayout.LineRects(selected);
        string text = PdfTextLayout.TextOf(selected);
        int page = slot.Index;
        var color = AnnotationColor.FromHex(kind == AnnotationKind.Highlight ? App.Settings.HighlightColor : App.Settings.CommentColor);
        var style = new AnnotationStyle(color, LineWidth: 1);
        string author = App.Settings.CommentAuthor;
        ClearCopySelection();
        string label = kind switch { AnnotationKind.Highlight => "Realçar", AnnotationKind.Underline => "Sublinhar", _ => "Riscar" };
        await ApplyEditAsync((input, output) => PdfAnnotations.AddTextMarkup(input, page, kind, lines, style, author, output, text), label: label);
    }

    private void SearchFor(string text)
    {
        ClearCopySelection();
        OpenSearch();
        SearchBox.Text = text.Length > 200 ? text[..200] : text; // a pesquisa começa sozinha ao mudar o texto
    }

    // ---------------- Página ----------------

    private void ShowPageMenu(DrawingSurface surface, PageSlot slot, Point p)
    {
        int i = slot.Index, n = _slots.Count, number = i + 1;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = $"Página {number} de {n}", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(MenuItem("Girar para a direita", "", () => _ = PageMenuEditAsync("Girar página",
            (a, b) => PdfOperations.RotatePage(a, i, 90, b), i, $"Página {number} girada")));
        menu.Items.Add(MenuItem("Girar para a esquerda", "", () => _ = PageMenuEditAsync("Girar página",
            (a, b) => PdfOperations.RotatePage(a, i, -90, b), i, $"Página {number} girada")));
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(MenuItem("Mover para cima", "", () => _ = MovePageAsync(i, i - 1), enabled: i > 0));
        menu.Items.Add(MenuItem("Mover para baixo", "", () => _ = MovePageAsync(i, i + 1), enabled: i < n - 1));
        var moveMore = new MenuFlyoutSubItem { Text = "Mover para…", Icon = new FontIcon { Glyph = "" }, IsEnabled = n > 2 };
        moveMore.Items.Add(MenuItem("O início", "", () => _ = MovePageAsync(i, 0), enabled: i > 0));
        moveMore.Items.Add(MenuItem("O fim", "", () => _ = MovePageAsync(i, n - 1), enabled: i < n - 1));
        menu.Items.Add(moveMore);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(MenuItem("Duplicar página", "", () => _ = PageMenuEditAsync("Duplicar página",
            (a, b) => PdfOperations.CopyPages(a, $"{number}", 1, number, b), i + 1, $"Página {number} duplicada")));
        var blank = new MenuFlyoutSubItem { Text = "Inserir página em branco", Icon = new FontIcon { Glyph = "" } };
        blank.Items.Add(MenuItem("Antes desta", "", () => _ = PageMenuEditAsync("Inserir página",
            (a, b) => PdfOperations.InsertBlankPages(a, i, 1, b), i, "Página em branco inserida")));
        blank.Items.Add(MenuItem("Depois desta", "", () => _ = PageMenuEditAsync("Inserir página",
            (a, b) => PdfOperations.InsertBlankPages(a, i + 1, 1, b), i + 1, "Página em branco inserida")));
        menu.Items.Add(blank);
        menu.Items.Add(MenuItem("Salvar esta página como PDF…", "", () => _ = ExtractOnePageAsync(i)));
        menu.Items.Add(new MenuFlyoutSeparator());

        var delete = MenuItem("Apagar página", "", () => _ = PageMenuEditAsync("Apagar página",
            (a, b) => PdfOperations.DeletePages(a, $"{number}", b), Math.Min(i, n - 2), $"Página {number} apagada"),
            enabled: n > 1);
        if (n <= 1) ToolTipService.SetToolTip(delete, "O PDF precisa ter ao menos uma página.");
        menu.Items.Add(delete);

        menu.ShowAt(surface, new FlyoutShowOptions { Position = p });
    }

    private Task MovePageAsync(int from, int to) => PageMenuEditAsync("Mover página",
        (a, b) => PdfOperations.MovePage(a, from, to, b), to, $"Página {from + 1} agora é a {to + 1}");

    /// <summary>Aplica a ação no documento aberto (dá para desfazer) e mostra a página <paramref name="goTo"/>.</summary>
    private async Task PageMenuEditAsync(string label, Action<string, string> operation, int goTo, string done)
    {
        if (DisplayPath is { } current && await IsSignedAsync(current) && !await ConfirmSignedChangeAsync()) return;
        if (!await ApplyEditAsync(operation, label: label)) return;
        if (_document is { } doc) RebuildPages(doc, Math.Clamp(goTo, 0, Math.Max(0, _slots.Count - 1)));
        ShowStatus(InfoBarSeverity.Success, done, "Dá para desfazer (Ctrl+Z). Salve (Ctrl+S) para gravar no arquivo.");
    }

    private async Task<bool> ConfirmSignedChangeAsync()
    {
        var dialog = Prepare(new ContentDialog
        {
            Title = "Este PDF está assinado",
            Content = new TextBlock
            {
                Text = "Mudar as páginas faz a assinatura deixar de valer. Quer continuar mesmo assim?",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Continuar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ExtractOnePageAsync(int index)
    {
        if (_path is null || DisplayPath is not { } input) return;
        string? target = await FilePickers.SavePdfAsync($"{Path.GetFileNameWithoutExtension(_path)}-pagina-{index + 1}");
        if (target is null) return;
        if (SamePath(target, _path))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escolha outro nome", "A página salva não pode substituir o arquivo aberto.");
            return;
        }
        try
        {
            await Task.Run(() => PdfOperations.ExtractPages(input, $"{index + 1}", target));
            ShowStatus(InfoBarSeverity.Success, "Página salva", $"Página {index + 1} salva em {target}.",
                "Abrir o arquivo", async () => await OpenAsync(target));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar a página", ex.Message);
        }
    }
}
