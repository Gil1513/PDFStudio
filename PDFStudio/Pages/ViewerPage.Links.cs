using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Etapa 5.3: links. Com a ferramenta "Link": arrastar cria, clicar num link existente edita ou exclui.
// Sem ferramenta ligada: o mouse vira "mãozinha" sobre um link e o clique segue o link.
public sealed partial class ViewerPage
{
    // ---------------- Links de cada página (lidos uma vez por versão do arquivo) ----------------

    private readonly Dictionary<int, Task<List<LinkInfo>>> _linkCache = [];
    private string? _linkCachePath;

    private Task<List<LinkInfo>> LinksOfPage(int page)
    {
        string path = DisplayPath!;
        if (_linkCachePath != path) { _linkCache.Clear(); _linkCachePath = path; }
        if (!_linkCache.TryGetValue(page, out var task))
            _linkCache[page] = task = Task.Run(() =>
            {
                try { return PdfAnnotations.ListLinks(path, page); }
                catch (Exception) { return new List<LinkInfo>(); } // PDF com links estranhos: só não mostra
            });
        return task;
    }

    /// <summary>Link sob o ponto (DIPs da superfície), se as posições já foram lidas; senão, lê e devolve null.</summary>
    private LinkInfo? LinkAt(PageSlot slot, Point p)
    {
        if (DisplayPath is null) return null;
        var task = LinksOfPage(slot.Index);
        if (!task.IsCompletedSuccessfully) return null;
        double x = p.X / DipsPerPoint, y = p.Y / DipsPerPoint;
        // O último da lista fica por cima (é o que o leitor de PDF usa ao clicar).
        return task.Result.LastOrDefault(l => x >= l.X && x <= l.X + l.Width && y >= l.Y && y <= l.Y + l.Height);
    }

    // ---------------- Sem ferramenta: mãozinha e seguir o link ----------------

    private InputSystemCursorShape? _plainCursor; // cursor atual sem ferramenta: mãozinha (link), I (texto) ou seta

    private void UpdateLinkCursor(DrawingSurface surface, Point p)
    {
        if (SlotOf(surface) is not { } slot) return;
        InputSystemCursorShape? cursor = LinkAt(slot, p) is { Target: not null } ? InputSystemCursorShape.Hand
            : IsOverText(slot, p) ? InputSystemCursorShape.IBeam : null;
        if (cursor == _plainCursor) return;
        _plainCursor = cursor;
        surface.SetCursor(cursor);
    }

    /// <summary>Segue o link sob o clique. Devolve false se não havia link ali.</summary>
    private async Task<bool> FollowLinkAsync(PageSlot slot, Point p)
    {
        if (LinkAt(slot, p) is not { Target: { } target }) return false;
        if (target.Page is { } page)
        {
            GoToPage(page);
            return true;
        }
        string url = target.Url ?? "";
        // Por segurança, só abre sites e e-mails, e sempre pergunta antes (o PDF pode ter vindo de qualquer lugar).
        if (PdfAnnotations.NormalizeUrl(url) is not { } safe || safe != url.Trim())
        {
            ShowStatus(InfoBarSeverity.Warning, "Link não aberto",
                $"Por segurança, o PDF Studio só abre sites (http/https) e e-mails. Este link aponta para: {url}");
            return true;
        }
        var dialog = Prepare(new ContentDialog
        {
            Title = "Abrir este endereço?",
            Content = new TextBlock { Text = $"O link leva para:\n{safe}\n\nAbra apenas se confiar neste documento.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = "Abrir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        });
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchUriAsync(new Uri(safe));
        return true;
    }

    // ---------------- Ferramenta "Link" ----------------

    private readonly List<(DrawingSurface Surface, Shapes.Rectangle Box)> _linkOutlines = [];
    private DrawingSurface? _linkOutlineSurface;

    /// <summary>Mostra o contorno dos links da página (só com a ferramenta Link ligada).</summary>
    private async Task ShowLinkOutlinesAsync(DrawingSurface surface, bool force = false)
    {
        if (!force && ReferenceEquals(surface, _linkOutlineSurface)) return;
        if (SlotOf(surface) is not { } slot || DisplayPath is null) return;
        RemoveLinkOutlines();
        _linkOutlineSurface = surface;
        var links = await LinksOfPage(slot.Index);
        if (_tool?.Mode != DrawMode.Link || !ReferenceEquals(surface, _linkOutlineSurface)) return;
        var accent = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        foreach (var link in links)
        {
            var box = new Shapes.Rectangle
            {
                Width = link.Width * DipsPerPoint,
                Height = link.Height * DipsPerPoint,
                Stroke = accent,
                StrokeThickness = 1.5,
                StrokeDashArray = [3, 2],
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(28, accent.Color.R, accent.Color.G, accent.Color.B)),
                IsHitTestVisible = false,
            };
            ToolTipService.SetToolTip(box, Describe(link.Target));
            Canvas.SetLeft(box, link.X * DipsPerPoint);
            Canvas.SetTop(box, link.Y * DipsPerPoint);
            surface.Children.Add(box);
            _linkOutlines.Add((surface, box));
        }
    }

    private void RemoveLinkOutlines()
    {
        foreach (var (surface, box) in _linkOutlines) surface.Children.Remove(box);
        _linkOutlines.Clear();
        _linkOutlineSurface = null;
    }

    private static string Describe(LinkTarget? target) =>
        target is null ? "Destino que o PDF Studio não reconhece"
        : target.Page is { } page ? $"Vai para a página {page + 1}"
        : target.Url!.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? $"E-mail para {target.Url[7..]}"
        : target.Url!;

    /// <summary>Área arrastada: pergunta o destino e cria o link.</summary>
    private async Task CreateLinkAsync(DrawingSurface surface, PageSlot slot, Rect area)
    {
        var (action, target) = await AskLinkTargetAsync(null, editing: false);
        if (action != LinkDialogAction.Save || target is null) return;
        int page = slot.Index;
        if (await ApplyEditAsync((input, output) => PdfAnnotations.AddLink(input, page,
                area.X / DipsPerPoint, area.Y / DipsPerPoint, area.Width / DipsPerPoint, area.Height / DipsPerPoint, target, output), label: "Adicionar link"))
            await ShowLinkOutlinesAsync(surface, force: true);
    }

    /// <summary>Clique num link existente com a ferramenta Link: trocar o destino ou excluir.</summary>
    private async Task EditLinkAsync(DrawingSurface surface, LinkInfo link)
    {
        var (action, target) = await AskLinkTargetAsync(link.Target, editing: true);
        int page = link.PageIndex, index = link.Index;
        bool changed = action switch
        {
            LinkDialogAction.Save when target is not null && target != link.Target =>
                await ApplyEditAsync((input, output) => PdfAnnotations.SetLinkTarget(input, page, index, target, output), label: "Mudar destino do link"),
            LinkDialogAction.Delete =>
                await ApplyEditAsync((input, output) => PdfAnnotations.Delete(input, page, index, output), label: "Excluir link"),
            _ => false,
        };
        if (changed) await ShowLinkOutlinesAsync(surface, force: true);
    }

    private enum LinkDialogAction { Cancel, Save, Delete }

    /// <summary>Janela "Link": site/e-mail ou página do documento. Ao editar, mostra também "Excluir link".</summary>
    private async Task<(LinkDialogAction, LinkTarget?)> AskLinkTargetAsync(LinkTarget? current, bool editing)
    {
        var toSite = new RadioButton { Content = "Site ou e-mail", GroupName = "linkKind", IsChecked = current?.Page is null };
        var toPage = new RadioButton { Content = "Página deste documento", GroupName = "linkKind", IsChecked = current?.Page is not null };
        var urlBox = new TextBox { PlaceholderText = "ex.: www.site.com.br ou nome@email.com", Text = current?.Url ?? "", Width = 340 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(urlBox, "Endereço do link");
        var pageBox = new NumberBox
        {
            Minimum = 1, Maximum = Math.Max(1, _slots.Count), Value = (current?.Page ?? CurrentIndex) + 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pageBox, "Página de destino");
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void Sync()
        {
            urlBox.Visibility = toSite.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            pageBox.Visibility = toPage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            error.Visibility = Visibility.Collapsed;
        }
        toSite.Checked += (_, _) => Sync();
        toPage.Checked += (_, _) => Sync();
        Sync();

        var dialog = Prepare(new ContentDialog
        {
            Title = editing ? "Editar link" : "Novo link",
            Content = new StackPanel { Spacing = 10, Children = { toSite, toPage, urlBox, pageBox, error } },
            PrimaryButtonText = "Salvar",
            SecondaryButtonText = editing ? "Excluir link" : "",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        LinkTarget? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (toPage.IsChecked == true)
            {
                if (double.IsNaN(pageBox.Value)) { args.Cancel = true; return; }
                result = LinkTarget.ToPage(Math.Clamp((int)pageBox.Value, 1, Math.Max(1, _slots.Count)) - 1);
                return;
            }
            if (PdfAnnotations.NormalizeUrl(urlBox.Text) is not { } url)
            {
                error.Text = "Endereço inválido. Digite um site (ex.: www.site.com.br) ou um e-mail.";
                error.Visibility = Visibility.Visible;
                args.Cancel = true; // mantém a janela aberta para corrigir
                return;
            }
            result = LinkTarget.ToUrl(url);
        };
        urlBox.Loaded += (_, _) => { if (toSite.IsChecked == true) urlBox.Focus(FocusState.Programmatic); };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => (LinkDialogAction.Save, result),
            ContentDialogResult.Secondary => (LinkDialogAction.Delete, null),
            _ => (LinkDialogAction.Cancel, null),
        };
    }
}
