using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

/// <summary>Função 4: assinatura digital com certificado .pfx, visível ou invisível.</summary>
public sealed partial class SignPage : Page
{
    private string? _pdf;
    private string? _pfx;
    private CertificateSummary? _cert;
    private Action? _statusAction;

    public SignPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    // ---------- Documento ----------

    private async void PickPdf_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is null) return;
        try
        {
            var (count, signed) = await Task.Run(() => (PdfOperations.GetPageCount(path), PdfSigning.HasSignatures(path)));
            _pdf = path;
            PdfText.Text = $"{Path.GetFileName(path)} · {count} {(count == 1 ? "página" : "páginas")}";
            PageBox.Maximum = count;
            PageBox.Value = Math.Min(PageBox.Value, count);
            AlreadySignedBar.IsOpen = signed;
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível abrir o PDF", ex.Message);
        }
        UpdateState();
    }

    // ---------- Certificado ----------

    private async void PickPfx_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickFileAsync(".pfx", ".p12");
        if (path is null) return;
        _pfx = path;
        _cert = null;
        PfxText.Text = Path.GetFileName(path);
        CertBar.IsOpen = false;
        PasswordInput.Focus(FocusState.Programmatic);
        UpdateState();
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _cert = null; // senha mudou: precisa verificar de novo
        CertBar.IsOpen = false;
        UpdateState();
    }

    private async void CheckCert_Click(object sender, RoutedEventArgs e) => await CheckCertificateAsync();

    private async Task<bool> CheckCertificateAsync()
    {
        if (_pfx is null) return false;
        string pfx = _pfx, password = PasswordInput.Password;
        try
        {
            _cert = await Task.Run(() => PdfSigning.InspectCertificate(pfx, password));
            CertBar.Title = _cert.Name;
            CertBar.Message = $"Emitido por: {_cert.Issuer}\nVálido de {_cert.NotBefore:dd/MM/yyyy} até {_cert.NotAfter:dd/MM/yyyy}";
            if (!_cert.HasPrivateKey)
            {
                CertBar.Severity = InfoBarSeverity.Error;
                CertBar.Message += "\nEste arquivo não contém a chave privada e não pode ser usado para assinar.";
            }
            else if (!_cert.IsValidNow)
            {
                CertBar.Severity = InfoBarSeverity.Warning;
                CertBar.Message += "\nAtenção: o certificado está fora do prazo de validade; a assinatura será considerada inválida pelos leitores de PDF.";
            }
            else
            {
                CertBar.Severity = InfoBarSeverity.Success;
            }
        }
        catch (Exception ex)
        {
            _cert = null;
            CertBar.Severity = InfoBarSeverity.Error;
            CertBar.Title = "Certificado não aberto";
            CertBar.Message = ex.Message;
        }
        CertBar.IsOpen = true;
        UpdateState();
        UpdatePreview();
        return _cert is { HasPrivateKey: true };
    }

    // ---------- Opções ----------

    private void KindRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VisibleOptions is null) return; // durante InitializeComponent
        VisibleOptions.Visibility = KindRadios.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void StampText_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void StampOption_Click(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>Atualiza a prévia do carimbo com o mesmo texto que será desenhado no PDF.</summary>
    private void UpdatePreview()
    {
        if (PreviewTitle is null || ContactBox is null) return; // durante InitializeComponent
        PreviewTitle.Text = string.IsNullOrWhiteSpace(StampTitleBox.Text) ? PdfSigning.DefaultStampTitle : StampTitleBox.Text.Trim();
        PreviewName.Text = !string.IsNullOrWhiteSpace(StampNameBox.Text) ? StampNameBox.Text.Trim()
            : _cert?.Name ?? "(nome do certificado)";

        var lines = new List<string>();
        if (StampDateCheck.IsChecked == true) lines.Add($"Data: {DateTime.Now:dd/MM/yyyy HH:mm:ss zzz}");
        if (StampDetailsCheck.IsChecked == true)
        {
            if (NullIfEmpty(ReasonBox.Text) is { } r) lines.Add($"Motivo: {r}");
            if (NullIfEmpty(LocationBox.Text) is { } l) lines.Add($"Local: {l}");
            if (NullIfEmpty(ContactBox.Text) is { } c) lines.Add($"Contato: {c}");
        }
        PreviewLines.Text = string.Join("\n", lines);
    }

    private void UpdateState()
    {
        CheckCertButton.IsEnabled = _pfx is not null;
        SignButton.IsEnabled = _pdf is not null && _pfx is not null && !SignProgress.IsActive;
    }

    // ---------- Assinar ----------

    private async void Sign_Click(object sender, RoutedEventArgs e)
    {
        if (_pdf is null || _pfx is null) return;
        if (_cert is null && !await CheckCertificateAsync()) return;
        if (_cert is { HasPrivateKey: false }) return;

        string tsa = TsaBox.Text.Trim();
        if (tsa.Length > 0 && !Uri.TryCreate(tsa, UriKind.Absolute, out _))
        {
            ShowStatus(InfoBarSeverity.Warning, "Endereço inválido", "O endereço do servidor de carimbo de tempo não é uma URL válida.");
            return;
        }

        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_pdf) + "-assinado");
        if (output is null) return;

        var request = new SignatureRequest
        {
            InputPath = _pdf,
            OutputPath = output,
            PfxPath = _pfx,
            PfxPassword = PasswordInput.Password,
            Visible = KindRadios.SelectedIndex == 1,
            PageNumber = double.IsNaN(PageBox.Value) ? 1 : (int)PageBox.Value,
            Position = (SignaturePosition)PositionBox.SelectedIndex,
            Reason = NullIfEmpty(ReasonBox.Text),
            Location = NullIfEmpty(LocationBox.Text),
            ContactInfo = NullIfEmpty(ContactBox.Text),
            TimestampUrl = NullIfEmpty(tsa),
            StampTitle = NullIfEmpty(StampTitleBox.Text),
            StampSignerName = NullIfEmpty(StampNameBox.Text),
            StampShowDate = StampDateCheck.IsChecked == true,
            StampShowDetails = StampDetailsCheck.IsChecked == true,
        };

        SignProgress.IsActive = true;
        UpdateState();
        try
        {
            var cert = await Task.Run(() => PdfSigning.SignAsync(request));
            ShowStatus(InfoBarSeverity.Success, "Documento assinado",
                $"Assinado por {cert.Name} ({(request.Visible ? $"visível, página {request.PageNumber}" : "invisível")}). Salvo em {output}.",
                "Abrir no visualizador", () => App.MainWindow!.OpenInViewer(output));
        }
        catch (Exception ex)
        {
            string message = ex.InnerException is System.Net.Http.HttpRequestException
                ? "Não foi possível contatar o servidor de carimbo de tempo. Verifique a internet ou deixe o campo em branco."
                : ex.Message;
            ShowStatus(InfoBarSeverity.Error, "Não foi possível assinar", message);
        }
        finally
        {
            SignProgress.IsActive = false;
            UpdateState();
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowStatus(InfoBarSeverity severity, string title, string message, string? actionText = null, Action? action = null)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        _statusAction = action;
        StatusAction.Content = actionText;
        StatusAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.IsOpen = true;
    }

    private void StatusAction_Click(object sender, RoutedEventArgs e) => _statusAction?.Invoke();
}
