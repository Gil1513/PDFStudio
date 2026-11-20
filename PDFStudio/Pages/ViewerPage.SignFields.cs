using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Foundation;

namespace PDFStudio.Pages;

// Etapa 6.4: campos de assinatura. Arrastar (ou clicar) cria um campo "Assinar aqui";
// clicar num campo vazio oferece excluir. Assinar dentro dele: Proteger → Assinar digitalmente.
public sealed partial class ViewerPage
{
    /// <summary>Tamanho do campo criado com um clique (pontos).</summary>
    private const double DefaultSignFieldWidth = 180, DefaultSignFieldHeight = 50;

    private readonly Dictionary<int, Task<List<SignatureField>>> _signFieldCache = [];
    private string? _signFieldCachePath;

    private Task<List<SignatureField>> SignFieldsOfPage(int page)
    {
        string path = DisplayPath!;
        if (_signFieldCachePath != path) { _signFieldCache.Clear(); _signFieldCachePath = path; }
        if (!_signFieldCache.TryGetValue(page, out var task))
            _signFieldCache[page] = task = Task.Run(() =>
            {
                try { return PdfSignatureFields.List(path).Where(f => f.Page == page).ToList(); }
                catch (Exception) { return new List<SignatureField>(); }
            });
        return task;
    }

    /// <summary>Campo de assinatura sob o ponto (DIPs da superfície).</summary>
    private async Task<SignatureField?> SignFieldAtAsync(PageSlot slot, Point p)
    {
        if (DisplayPath is null) return null;
        double x = p.X / DipsPerPoint, y = p.Y / DipsPerPoint;
        return (await SignFieldsOfPage(slot.Index))
            .LastOrDefault(f => x >= f.X && x <= f.X + f.Width && y >= f.Y && y <= f.Y + f.Height);
    }

    /// <summary>Clique num campo existente: se está vazio, oferece excluir; se já foi assinado, só avisa.</summary>
    private async Task<bool> ClickSignFieldAsync(PageSlot slot, Point p)
    {
        if (await SignFieldAtAsync(slot, p) is not { } field) return false;
        if (field.Signed)
        {
            ShowStatus(InfoBarSeverity.Informational, "Campo já assinado",
                $"O campo “{field.Name}” já tem uma assinatura e não pode ser excluído (isso a invalidaria).");
            return true;
        }
        var dialog = Prepare(new ContentDialog
        {
            Title = $"Campo “{field.Name}”",
            Content = new TextBlock
            {
                Text = "Este campo de assinatura ainda está vazio. Para assinar dentro dele, use Proteger → Assinar digitalmente.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
            },
            PrimaryButtonText = "Excluir campo",
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return true;
        if (await ApplyEditAsync((input, output) => PdfSignatureFields.Remove(input, field.Name, output), label: "Excluir campo de assinatura"))
            ShowStatus(InfoBarSeverity.Success, "Campo excluído", $"“{field.Name}” saiu da página. Ctrl+Z desfaz.");
        return true;
    }

    /// <summary>Fim do arrasto: pergunta o nome e cria o campo (um clique usa o tamanho padrão).</summary>
    private async Task CreateSignFieldAsync(DrawingSurface surface, PageSlot slot, Rect box)
    {
        if (DisplayPath is not { } path) return;
        double x = box.X / DipsPerPoint, y = box.Y / DipsPerPoint, w = box.Width / DipsPerPoint, h = box.Height / DipsPerPoint;
        if (w < 6 && h < 6)
        {
            // Só um clique: campo do tamanho padrão a partir do ponto, sem passar da página.
            double pageW = surface.ActualWidth / DipsPerPoint, pageH = surface.ActualHeight / DipsPerPoint;
            w = DefaultSignFieldWidth;
            h = DefaultSignFieldHeight;
            x = Math.Max(0, Math.Min(x, pageW - w));
            y = Math.Max(0, Math.Min(y, pageH - h));
        }
        else
        {
            w = Math.Max(w, PdfSignatureFields.MinWidth);
            h = Math.Max(h, PdfSignatureFields.MinHeight);
        }

        string suggestion = await Task.Run(() =>
        {
            try { return PdfSignatureFields.SuggestName(path); }
            catch (Exception) { return "Assinatura1"; } // só uma sugestão: o nome é conferido ao criar o campo
        });
        var nameBox = new TextBox { Header = "Nome do campo", Text = suggestion, MinWidth = 300 };
        AutomationPropertiesHelper(nameBox, "Nome do campo");
        var dialog = Prepare(new ContentDialog
        {
            Title = "Campo de assinatura",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    nameBox,
                    new TextBlock
                    {
                        Text = "O campo aparece como “Assinar aqui”. Quem receber o PDF pode assinar dentro dele " +
                               "(aqui: Proteger → Assinar digitalmente).",
                        Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
                    },
                },
            },
            PrimaryButtonText = "Criar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
        dialog.Opened += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string name = nameBox.Text.Trim();
        int page = slot.Index;
        if (await ApplyEditAsync((input, output) => PdfSignatureFields.Add(input, page, x, y, w, h, name, output), label: "Adicionar campo de assinatura"))
            ShowStatus(InfoBarSeverity.Success, "Campo criado",
                $"“{name}” está na página {page + 1}. Salve com Ctrl+S; para assinar nele, use Proteger → Assinar digitalmente.");
    }

    private static void AutomationPropertiesHelper(FrameworkElement element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
