using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 5.4: painel "Anexos" — arquivos guardados dentro do PDF (anexar, abrir, salvar, excluir).
public sealed partial class ViewerPage
{
    private int _attachmentListVersion;

    private bool AttachmentsPanelOpen => AttachmentsToggle.IsChecked == true && CommentsToggle.IsChecked != true && _path is not null;

    private void AttachmentsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_commentPanelReady) return;
        if (AttachmentsToggle.IsChecked == true) ThumbnailsToggle.IsChecked = CommentsToggle.IsChecked = false; // um painel por vez
        UpdateSidePanel();
    }

    private AttachmentItem? SelectedAttachment => AttachmentList.SelectedItem as AttachmentItem;

    private async Task RefreshAttachmentListAsync()
    {
        if (!_commentPanelReady || !AttachmentsPanelOpen || DisplayPath is not { } path) return;
        int version = ++_attachmentListVersion;
        string? selected = SelectedAttachment?.Name;
        List<AttachmentInfo> all;
        try
        {
            all = await Task.Run(() => PdfAttachments.List(path));
        }
        catch (Exception ex)
        {
            AttachmentList.ItemsSource = null;
            AttachmentCountText.Text = $"Não foi possível ler os anexos: {ex.Message}";
            return;
        }
        if (version != _attachmentListVersion) return;
        var items = all.Select(a => new AttachmentItem(a)).ToList();
        AttachmentList.ItemsSource = items;
        if (selected is not null) AttachmentList.SelectedItem = items.FirstOrDefault(i => i.Name == selected);
        AttachmentCountText.Text = all.Count == 0
            ? "Este documento não tem anexos. Use \"Anexar\" para guardar um arquivo dentro do PDF."
            : Plural(all.Count, "anexo", "anexos") + $" · {AttachmentItem.FormatSize(all.Sum(a => a.Size))}";
        UpdateAttachmentActions();
    }

    private void AttachmentList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAttachmentActions();

    private void UpdateAttachmentActions()
    {
        var item = SelectedAttachment;
        OpenAttachmentButton.IsEnabled = SaveAttachmentButton.IsEnabled = item is not null;
        // Anexos presos à página são comentários de outro programa: excluir pelo painel de comentários.
        DeleteAttachmentButton.IsEnabled = item is { Info.PageIndex: null };
    }

    private async void AttachmentList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectedAttachment is not null) await OpenAttachmentAsync(SelectedAttachment.Info);
    }

    // ---------------- Ações ----------------

    private async void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPath is null) return;
        string? file = await FilePickers.PickFileAsync("*");
        if (file is null) return;
        var description = new TextBox { PlaceholderText = "Descrição (opcional)", AcceptsReturn = false, MaxLength = 200 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(description, "Descrição do anexo");
        long size = new FileInfo(file).Length;
        var dialog = Prepare(new ContentDialog
        {
            Title = "Anexar arquivo",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"{Path.GetFileName(file)} ({AttachmentItem.FormatSize(size)})", TextWrapping = TextWrapping.Wrap },
                    description,
                },
            },
            PrimaryButtonText = "Anexar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string text = description.Text;
        string? name = null;
        if (await ApplyEditAsync((input, output) => name = PdfAttachments.Add(input, file, text, output)))
        {
            ShowStatus(InfoBarSeverity.Success, "Arquivo anexado", $"\"{name}\" agora faz parte do PDF (salve para gravar).");
            AttachmentsToggle.IsChecked = true;
        }
    }

    private async void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAttachment is { } item) await OpenAttachmentAsync(item.Info);
    }

    /// <summary>Abre o anexo no programa padrão (pergunta antes; programas e scripts só podem ser salvos).</summary>
    private async Task OpenAttachmentAsync(AttachmentInfo info)
    {
        if (DisplayPath is not { } path) return;
        if (PdfAttachments.IsRisky(info.Name))
        {
            ShowStatus(InfoBarSeverity.Warning, "Este anexo não é aberto pelo PDF Studio",
                $"\"{info.Name}\" é um programa ou script e pode prejudicar o computador. Se confiar nele, use \"Salvar anexo como\".");
            return;
        }
        var dialog = Prepare(new ContentDialog
        {
            Title = "Abrir o anexo?",
            Content = new TextBlock
            {
                Text = $"\"{info.Name}\" será aberto no programa padrão do Windows.\n\nAbra apenas se confiar neste documento.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Abrir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            // Cada abertura numa pasta própria em %TEMP%\PDFStudio\anexos (o nome do arquivo é preservado).
            string folder = Path.Combine(Path.GetTempPath(), "PDFStudio", "anexos", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, info.Name);
            await Task.Run(() => PdfAttachments.SaveTo(path, info, target));
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(target);
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
                ShowStatus(InfoBarSeverity.Warning, "Nenhum programa abriu o anexo", "Use \"Salvar anexo como\" e abra pelo Explorador de Arquivos.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível abrir o anexo", ex.Message);
        }
    }

    private async void SaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAttachment is not { } item || DisplayPath is not { } path) return;
        string? target = await FilePickers.SaveFileAsync(item.Name);
        if (target is null) return;
        try
        {
            await Task.Run(() => PdfAttachments.SaveTo(path, item.Info, target));
            ShowStatus(InfoBarSeverity.Success, "Anexo salvo", target);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar o anexo", ex.Message);
        }
    }

    private async void DeleteAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAttachment is not { Info: { PageIndex: null } info }) return;
        if (await ApplyEditAsync((input, output) => PdfAttachments.Remove(input, info.Number, output)))
            ShowStatus(InfoBarSeverity.Success, "Anexo excluído", "Use Desfazer (Ctrl+Z) se quiser trazê-lo de volta.");
    }
}
