using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Dialogs;

/// <summary>
/// "Criar meu certificado": a pessoa digita só o nome (o "Assinado por") e uma senha; o app gera um
/// certificado digital .pfx autoassinado (Core/CertificateMaker, válido por 3 anos) e já o deixa escolhido
/// para assinar.
/// </summary>
public sealed class CreateCertificateDialog : ContentDialog
{
    /// <summary>Validade do certificado criado.</summary>
    private const int Years = 3;

    private readonly TextBox _name = new() { Header = "Assinado por *", PlaceholderText = "Seu nome, como vai aparecer na assinatura" };
    private readonly PasswordBox _password = new() { Header = "Senha do certificado *", PlaceholderText = $"pelo menos {CertificateMaker.MinPasswordLength} caracteres" };
    private readonly PasswordBox _confirm = new() { Header = "Repita a senha *" };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
    private readonly ProgressRing _ring = new() { Width = 20, Height = 20, IsActive = false };

    /// <param name="suggestedName">Nome para começar (ex.: o usuário do Windows).</param>
    public CreateCertificateDialog(string? suggestedName = null)
    {
        Title = "Criar meu certificado";
        PrimaryButtonText = "Criar e salvar…";
        CloseButtonText = "Cancelar";
        DefaultButton = ContentDialogButton.Primary;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 640.0;
        _name.Text = suggestedName ?? "";

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(_name, 2);
        Grid.SetRow(_password, 1);
        Grid.SetRow(_confirm, 1);
        Grid.SetColumn(_confirm, 1);
        grid.Children.Add(_name);
        grid.Children.Add(_password);
        grid.Children.Add(_confirm);

        Content = new ScrollViewer
        {
            MaxHeight = 600,
            Padding = new Thickness(0, 0, 12, 0),
            Content = new StackPanel
            {
                Width = 520,
                Spacing = 16,
                Children =
                {
                    new InfoBar
                    {
                        Severity = InfoBarSeverity.Informational,
                        IsOpen = true,
                        IsClosable = false,
                        Title = "Certificado próprio (autoassinado)",
                        Message = "Serve para assinar e provar que o documento não foi mudado depois. Os leitores de PDF mostram o seu nome, "
                            + "mas a identidade aparece como “desconhecida” até quem recebe confiar no certificado. Não substitui um "
                            + "certificado ICP-Brasil (e-CPF/e-CNPJ) quando a lei exige um.",
                    },
                    grid,
                    new TextBlock
                    {
                        Text = $"Vale por {Years} anos. A senha protege o arquivo do certificado: guarde-a, sem ela o certificado não pode ser usado. O PDF Studio não guarda a senha.",
                        FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _ring } },
                    _error,
                },
            },
        };
        PrimaryButtonClick += Primary_Click;
    }

    /// <summary>O arquivo criado (null se cancelou).</summary>
    public string? PfxPath { get; private set; }

    /// <summary>A senha digitada (para já deixar o certificado pronto na tela Assinar).</summary>
    public string Password => _password.Password;

    private CertificateIdentity Identity => new(_name.Text.Trim(), null, null, null, null, null, null);

    private async void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // fecha só depois de criar
        _error.IsOpen = false;
        var id = Identity;
        string? problem = CertificateMaker.Problem(id, _password.Password, Years);
        if (problem is null && _password.Password != _confirm.Password) problem = "As duas senhas não são iguais.";
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        string fileName = string.Concat(id.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        string? path = await FilePickers.SaveFileAsync((fileName.Length > 0 ? fileName : "certificado") + ".pfx");
        if (path is null) return;

        IsPrimaryButtonEnabled = false;
        _ring.IsActive = true;
        try
        {
            string password = _password.Password;
            await Task.Run(() => CertificateMaker.CreatePfx(id, password, Years, path));
            PfxPath = path;
            Hide();
        }
        catch (Exception ex)
        {
            ShowError(ex is UnauthorizedAccessException ? "Sem permissão para gravar nessa pasta. Escolha outra." : ex.Message);
        }
        finally
        {
            _ring.IsActive = false;
            IsPrimaryButtonEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        _error.Title = "Confira os dados";
        _error.Message = message;
        _error.IsOpen = true;
    }
}
