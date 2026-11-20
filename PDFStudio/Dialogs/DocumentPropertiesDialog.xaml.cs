using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Janela "Propriedades do documento" (Ctrl+D no visualizador).</summary>
public sealed partial class DocumentPropertiesDialog : ContentDialog
{
    private readonly DocumentProperties _props;

    /// <summary>true quando o arquivo foi regravado com novas propriedades.</summary>
    public bool Saved { get; private set; }

    public DocumentPropertiesDialog(DocumentProperties props)
    {
        InitializeComponent();
        _props = props;

        FileNameText.Text = Path.GetFileName(props.FilePath);
        PageCountText.Text = props.PageCount.ToString();
        UpdatePageSize();

        TitleBox.Text = props.Metadata.Title;
        AuthorBox.Text = props.Metadata.Author;
        SubjectBox.Text = props.Metadata.Subject;
        KeywordsBox.Text = props.Metadata.Keywords;
        CreatorText.Text = Dash(props.Creator);
        ProducerText.Text = Dash(props.Producer);

        VersionText.Text = $"Versão PDF: {props.PdfVersion}";
        LocationText.Text = props.FilePath;
        FileSizeText.Text = $"Tamanho do arquivo: {DocumentPropertiesService.FormatSize(props.FileSize)}";
        CreatedText.Text = $"Criado: {FormatDate(props.Created)}";
        ModifiedText.Text = $"Modificado: {FormatDate(props.Modified)}";
        if (props.IsEncrypted)
        {
            EncryptedText.Text = "Segurança: o arquivo é criptografado (pode ter restrições de edição).";
            EncryptedText.Visibility = Visibility.Visible;
        }

        if (props.Signatures.Count > 0)
        {
            SignedWarning.IsOpen = true;
            SetEditable(false);
            SignaturesSection.Visibility = Visibility.Visible;
            foreach (var sig in props.Signatures) SignaturesList.Children.Add(SignatureCard(sig));
        }
    }

    private void SetEditable(bool editable)
    {
        foreach (var box in new[] { TitleBox, AuthorBox, SubjectBox, KeywordsBox }) box.IsReadOnly = !editable;
    }

    private void UnlockCheck_Click(object sender, RoutedEventArgs e) => SetEditable(UnlockCheck.IsChecked == true);

    // ---------- Tamanho da página ----------

    private void UnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePageSize();

    private void UpdatePageSize()
    {
        if (_props is null || PageSizeText is null) return; // durante InitializeComponent
        var unit = (PageSizeUnit)Math.Max(0, UnitBox.SelectedIndex);
        string format = unit == PageSizeUnit.Points ? "0.##" : "0.000";
        PageSizeText.Text = _props.PageCount == 0 ? "—"
            : $"{DocumentPropertiesService.Convert(_props.PageWidthPt, unit).ToString(format)} x " +
              $"{DocumentPropertiesService.Convert(_props.PageHeightPt, unit).ToString(format)}";
    }

    // ---------- Salvar ----------

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var metadata = new DocumentMetadata(TitleBox.Text, AuthorBox.Text, SubjectBox.Text, KeywordsBox.Text);
        if (Same(metadata, _props.Metadata)) return; // nada mudou: só fecha

        var deferral = args.GetDeferral();
        try
        {
            string path = _props.FilePath;
            await Task.Run(() => DocumentPropertiesService.Save(path, metadata, path));
            Saved = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true; // mantém a janela aberta para mostrar o erro
            ErrorBar.Title = "Não foi possível salvar";
            ErrorBar.Message = ex is IOException
                ? "O arquivo está em uso por outro programa. Feche-o e tente de novo."
                : ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool Same(DocumentMetadata a, DocumentMetadata b) =>
        a.Title.Trim() == b.Title.Trim() && a.Author.Trim() == b.Author.Trim()
        && a.Subject.Trim() == b.Subject.Trim() && a.Keywords.Trim() == b.Keywords.Trim();

    private void LocationLink_Click(object sender, RoutedEventArgs e)
    {
        // Abre o Explorer com o arquivo selecionado.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_props.FilePath}\"") { UseShellExecute = true });
    }

    // ---------- Assinaturas ----------

    private static UIElement SignatureCard(SignatureDetails sig)
    {
        var (severity, status) = sig.IsIntact switch
        {
            true when sig.CoversWholeFile => (InfoBarSeverity.Success, "O documento não foi alterado desde esta assinatura."),
            true => (InfoBarSeverity.Informational, "A parte assinada está íntegra, mas o arquivo recebeu alterações depois da assinatura."),
            false => (InfoBarSeverity.Error, "O documento foi ALTERADO depois de assinado. A assinatura não é válida."),
            null => (InfoBarSeverity.Warning, "Não foi possível verificar a integridade desta assinatura."),
        };

        var lines = new List<string>();
        if (sig.Issuer is { } issuer) lines.Add($"Emitido por: {issuer}");
        if (sig.SignedAt is { } at) lines.Add($"Data: {at:dd/MM/yyyy HH:mm:ss}");
        if (sig.Reason is { } reason) lines.Add($"Razão: {reason}");
        if (sig.Location is { } location) lines.Add($"Localização: {location}");
        lines.Add(sig.PageNumber is { } page ? $"Campo: {sig.FieldName} (página {page})" : $"Campo: {sig.FieldName} (invisível)");
        lines.Add(status);

        return new InfoBar
        {
            Title = $"Assinado por {sig.Signer ?? "(desconhecido)"}",
            Message = string.Join("\n", lines),
            Severity = severity,
            IsOpen = true,
            IsClosable = false,
        };
    }

    private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
    private static string FormatDate(DateTime? d) => d is { } v ? v.ToString("dd/MM/yyyy HH:mm:ss") : "—";
}
