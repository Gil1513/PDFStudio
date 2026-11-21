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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        PasswordInput.Password = ""; // saiu da tela: esquece a senha do certificado
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Aberto pelo visualizador: já seleciona o arquivo que estava aberto.
        if (e.Parameter is string path && path.Length > 0 && !string.Equals(path, _pdf, StringComparison.OrdinalIgnoreCase))
            await LoadPdfAsync(path);
    }

    private async void PickPdf_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is not null) await LoadPdfAsync(path);
    }

    private async Task LoadPdfAsync(string path)
    {
        try
        {
            var (count, signed, fields) = await Task.Run(() => (PdfOperations.GetPageCount(path), PdfSigning.HasSignatures(path),
                PdfSignatureFields.List(path).Where(f => !f.Signed).ToList()));
            ShowFields(fields);
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

    // ---------- Campos de assinatura vazios (Etapa 6.4) ----------

    private List<SignatureField> _emptyFields = [];

    /// <summary>Se o PDF tem campos "Assinar aqui" vazios, oferece assinar dentro de um deles (já escolhe o primeiro).</summary>
    private void ShowFields(List<SignatureField> fields)
    {
        _emptyFields = fields;
        FieldBox.Items.Clear();
        if (fields.Count == 0)
        {
            FieldBox.Visibility = Visibility.Collapsed;
            PlacePanel.Visibility = Visibility.Visible;
            return;
        }
        FieldBox.Items.Add("Escolher a página e o canto");
        foreach (var f in fields) FieldBox.Items.Add($"Campo “{f.Name}” (página {f.Page + 1})");
        FieldBox.Visibility = Visibility.Visible;
        FieldBox.SelectedIndex = 1;
        KindRadios.SelectedIndex = 1; // campo de assinatura = assinatura visível
    }

    private void FieldBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PlacePanel.Visibility = SelectedField is null ? Visibility.Visible : Visibility.Collapsed;

    private SignatureField? SelectedField =>
        FieldBox.Visibility == Visibility.Visible && FieldBox.SelectedIndex >= 1 && FieldBox.SelectedIndex <= _emptyFields.Count
            ? _emptyFields[FieldBox.SelectedIndex - 1] : null;

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

    /// <summary>Gera um certificado com os dados digitados e já o deixa escolhido (com a senha) para assinar.</summary>
    private async void CreateCert_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PDFStudio.Dialogs.CreateCertificateDialog(App.Settings.CommentAuthor) { XamlRoot = XamlRoot, RequestedTheme = App.Settings.Theme };
        await dialog.ShowAsync();
        if (dialog.PfxPath is not { } path) return;
        _pfx = path;
        _cert = null;
        PfxText.Text = Path.GetFileName(path);
        PasswordInput.Password = dialog.Password;
        UpdateState();
        if (await CheckCertificateAsync())
            CertBar.Message += "\nCertificado criado agora e já escolhido. Guarde o arquivo .pfx e a senha.";
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
        if (PreviewTitle is null || LocationBox is null) return; // durante InitializeComponent
        PreviewTitle.Text = string.IsNullOrWhiteSpace(StampTitleBox.Text) ? PdfSigning.DefaultStampTitle : StampTitleBox.Text.Trim();
        PreviewName.Text = !string.IsNullOrWhiteSpace(StampNameBox.Text) ? StampNameBox.Text.Trim()
            : _cert?.Name ?? "(nome do certificado)";

        var lines = new List<string>();
        if (StampDateCheck.IsChecked == true) lines.Add($"Data: {DateTime.Now:dd/MM/yyyy HH:mm:ss zzz}");
        if (StampDetailsCheck.IsChecked == true)
        {
            if (NullIfEmpty(ReasonBox.Text) is { } r) lines.Add($"Razão: {r}");
            if (NullIfEmpty(LocationBox.Text) is { } l) lines.Add($"Localização: {l}");
        }
        PreviewLines.Text = string.Join("\n", lines);
    }

    private void UpdateState()
    {
        // "Assinado por" sempre vem do certificado (não dá para digitar outro nome).
        SignerBox.Text = _cert?.Name ?? "";
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
            FieldName = KindRadios.SelectedIndex == 1 ? SelectedField?.Name : null,
            Reason = NullIfEmpty(ReasonBox.Text),
            Location = NullIfEmpty(LocationBox.Text),
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
                $"Assinado por {cert.Name} ({(request.FieldName is { } field ? $"no campo “{field}”" : request.Visible ? $"visível, página {request.PageNumber}" : "invisível")}). Salvo em {output}.",
                "Abrir no visualizador", () => App.MainWindow!.OpenInViewer(output));
            PasswordInput.Password = ""; // a senha não fica na tela depois de usada
        }
        catch (Exception ex)
        {
            bool tsaFailed = request.TimestampUrl is not null
                && (ex is System.Net.Http.HttpRequestException or TaskCanceledException
                    || ex.InnerException is System.Net.Http.HttpRequestException or TaskCanceledException);
            string message = tsaFailed
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
