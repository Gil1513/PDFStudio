using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>
/// Propriedades de uma assinatura digital do documento: se é válida, desconhecida ou inválida, quem assinou,
/// razão, data, localização, o certificado e um resumo explicando cada ponto. "Confiar neste certificado"
/// guarda a impressão digital nas preferências (vale só no PDF Studio deste computador).
/// </summary>
public sealed partial class SignaturePropertiesDialog : ContentDialog
{
    private IReadOnlyList<SignatureDetails> _signatures;
    private readonly Func<Task<IReadOnlyList<SignatureDetails>>>? _recheckOnline;
    private SignatureDetails? _current;

    /// <param name="recheckOnline">Lê as assinaturas de novo consultando a internet sobre cancelamento (opcional).</param>
    public SignaturePropertiesDialog(IReadOnlyList<SignatureDetails> signatures, int start = 0,
        Func<Task<IReadOnlyList<SignatureDetails>>>? recheckOnline = null)
    {
        InitializeComponent();
        _signatures = signatures;
        _recheckOnline = recheckOnline;
        if (signatures.Count > 1)
        {
            foreach (var s in signatures)
                SignatureBox.Items.Add($"{s.FieldName} — {s.Signer ?? "(desconhecido)"}{(s.PageNumber is { } p ? $" (página {p})" : " (invisível)")}");
            SignatureBox.Visibility = Visibility.Visible;
        }
        if (signatures.Count > 0)
        {
            if (SignatureBox.Visibility == Visibility.Visible) SignatureBox.SelectedIndex = Math.Clamp(start, 0, signatures.Count - 1);
            else Show(signatures[0]);
        }
    }

    private void SignatureBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SignatureBox.SelectedIndex >= 0) Show(_signatures[SignatureBox.SelectedIndex]);
    }

    private static bool IsTrusted(SignerCertificate? cert) =>
        cert is not null && App.Settings.TrustedCertificates.Contains(cert.Sha256, StringComparer.OrdinalIgnoreCase);

    private void Show(SignatureDetails sig)
    {
        _current = sig;
        var cert = sig.Certificate;
        var report = SignatureReview.Evaluate(sig, IsTrusted(cert));

        HeadlineText.Text = report.Headline;
        (StatusIcon.Glyph, StatusBadge.Background) = report.Status switch
        {
            SignatureStatus.Valid => ("", Brush(0x2E, 0x7D, 0x32)),
            SignatureStatus.ValidWithChanges => ("", Brush(0x2E, 0x7D, 0x32)),
            SignatureStatus.Invalid => ("", Brush(0xC6, 0x28, 0x28)),
            _ => ("", Brush(0xEF, 0x6C, 0x00)),
        };
        FieldText.Text = sig.PageNumber is { } page
            ? $"Campo “{sig.FieldName}”, na página {page}"
            : $"Campo “{sig.FieldName}” (assinatura invisível)";

        SignerText.Text = sig.Signer ?? cert?.Name ?? "(desconhecido)";
        ReasonText.Text = sig.Reason ?? "Não informado";
        DateText.Text = sig.SignedAt is { } at ? at.ToString("dd/MM/yyyy HH:mm:ss") : "Não informada";
        LocationText.Text = sig.Location ?? "Não informado";

        TrustButton.IsEnabled = cert is not null && !cert.IsTrustedBySystem;
        TrustButton.Content = IsTrusted(cert) ? "Deixar de confiar" : "Confiar neste certificado";
        TrustNote.Visibility = TrustButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        RevocationButton.Visibility = _recheckOnline is not null && cert?.Revocation == RevocationCheck.NotChecked
            ? Visibility.Visible : Visibility.Collapsed;
        ShowCertButton.IsEnabled = cert is not null;
        if (cert is null) ShowCertButton.IsChecked = false;
        FillCertificate(cert);

        SummaryPanel.Children.Clear();
        foreach (var line in report.Summary)
            SummaryPanel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
    }

    private void FillCertificate(SignerCertificate? cert)
    {
        CertGrid.Children.Clear();
        CertGrid.RowDefinitions.Clear();
        if (cert is null) return;
        var rows = new List<(string, string)>
        {
            ("Nome", cert.Name),
        };
        if (cert.Email is { } e) rows.Add(("E-mail", e));
        if (cert.Organization is { } o) rows.Add(("Organização", o));
        if (cert.OrganizationalUnit is { } ou) rows.Add(("Departamento", ou));
        string place = string.Join(", ", new[] { cert.City, cert.State, cert.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (place.Length > 0) rows.Add(("Local", place));
        rows.Add(("Emitido por", cert.IsSelfSigned ? $"{cert.IssuerName} (autoassinado: a própria pessoa)" : cert.IssuerName));
        rows.Add(("Válido", $"de {cert.NotBefore:dd/MM/yyyy HH:mm} até {cert.NotAfter:dd/MM/yyyy HH:mm}"));
        rows.Add(("Confiança", cert.IsTrustedBySystem ? "Autoridade certificadora confiável do Windows"
            : IsTrusted(cert) ? "Marcado como confiável por você" : "Não confiável (ainda)"));
        if (cert.Usages.Count > 0) rows.Add(("Uso", string.Join(", ", cert.Usages)));
        rows.Add(("Chave", $"{cert.KeyDescription} · {cert.SignatureAlgorithm}"));
        rows.Add(("Número de série", cert.SerialNumber));
        rows.Add(("SHA-256", Group(cert.Sha256)));
        rows.Add(("SHA-1", Group(cert.Sha1)));
        rows.Add(("Nome completo", cert.Subject));

        for (int i = 0; i < rows.Count; i++)
        {
            CertGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Item1 + ":", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            var value = new TextBlock { Text = rows[i].Item2, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            if (rows[i].Item1 is "SHA-256" or "SHA-1") value.FontFamily = new FontFamily("Consolas");
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            CertGrid.Children.Add(label);
            CertGrid.Children.Add(value);
        }
    }

    /// <summary>Hexadecimal em grupos de 2 ("AB CD EF…"), mais fácil de comparar de olho.</summary>
    private static string Group(string hex) => string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));

    private void ShowCert_Changed(object sender, RoutedEventArgs e) =>
        CertPanel.Visibility = ShowCertButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void Revocation_Click(object sender, RoutedEventArgs e)
    {
        if (_recheckOnline is null || _current is null) return;
        int index = Math.Max(0, IndexOf(_current));
        RevocationButton.IsEnabled = false;
        RevocationButton.Content = "Consultando…";
        try
        {
            var fresh = await _recheckOnline();
            if (index < fresh.Count)
            {
                _signatures = fresh;
                Show(fresh[index]);
            }
        }
        catch (Exception)
        {
            SummaryPanel.Children.Add(new TextBlock { Text = "Não foi possível consultar pela internet agora.", TextWrapping = TextWrapping.Wrap });
        }
        finally
        {
            RevocationButton.IsEnabled = true;
            RevocationButton.Content = "Conferir cancelamento (internet)";
        }
    }

    private int IndexOf(SignatureDetails sig)
    {
        for (int i = 0; i < _signatures.Count; i++) if (ReferenceEquals(_signatures[i], sig)) return i;
        return -1;
    }

    private void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Certificate is not { } cert) return;
        var list = App.Settings.TrustedCertificates;
        if (IsTrusted(cert)) list.RemoveAll(t => string.Equals(t, cert.Sha256, StringComparison.OrdinalIgnoreCase));
        else list.Add(cert.Sha256);
        App.Settings.Save();
        Show(_current);
    }
}
