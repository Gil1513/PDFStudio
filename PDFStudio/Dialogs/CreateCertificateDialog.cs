using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Dialogs;

/// <summary>
/// "Criar meu certificado": a pessoa digita os dados (nome, e-mail, organização, cidade…) e uma senha; o app
/// gera um certificado digital .pfx autoassinado com esses dados (Core/CertificateMaker) e já o deixa
/// escolhido para assinar.
/// </summary>
public sealed class CreateCertificateDialog : ContentDialog
{
    private readonly TextBox _name = Box("Nome de quem assina *", "Como vai aparecer em “Assinado por”");
    private readonly TextBox _email = Box("E-mail", "nome@exemplo.com");
    private readonly TextBox _organization = Box("Organização ou empresa", "");
    private readonly TextBox _unit = Box("Departamento", "");
    private readonly TextBox _city = Box("Cidade", "");
    private readonly TextBox _state = Box("Estado (UF)", "SP");
    private readonly TextBox _country = Box("País (sigla)", "BR");
    private readonly NumberBox _years = new()
    {
        Header = "Validade (anos)", Minimum = 1, Maximum = CertificateMaker.MaxYears, Value = 3,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1,
    };
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
        Resources["ContentDialogMaxWidth"] = 720.0;
        _name.Text = suggestedName ?? "";
        _country.Text = "BR";
        _country.MaxLength = 2;
        _state.MaxLength = 30;

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Put(FrameworkElement e, int row, int col, int span = 1)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            Grid.SetColumnSpan(e, span);
            grid.Children.Add(e);
        }
        Put(_name, 0, 0, 2);
        Put(_email, 1, 0);
        Put(_organization, 1, 1);
        Put(_unit, 2, 0);
        Put(_city, 2, 1);
        var small = new Grid { ColumnSpacing = 12 };
        small.ColumnDefinitions.Add(new ColumnDefinition());
        small.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_country, 1);
        small.Children.Add(_state);
        small.Children.Add(_country);
        Put(small, 3, 0);
        Put(_years, 3, 1);
        _years.HorizontalAlignment = HorizontalAlignment.Stretch;
        Put(_password, 4, 0);
        Put(_confirm, 4, 1);

        Content = new ScrollViewer
        {
            MaxHeight = 600,
            Padding = new Thickness(0, 0, 12, 0),
            Content = new StackPanel
            {
                Width = 600,
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
                        Text = "Guarde a senha: sem ela o certificado não pode ser usado. O PDF Studio não guarda a senha.",
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

    /// <summary>O e-mail digitado (para o campo "Contato").</summary>
    public string? Email => string.IsNullOrWhiteSpace(_email.Text) ? null : _email.Text.Trim();

    private static TextBox Box(string header, string placeholder) => new() { Header = header, PlaceholderText = placeholder };

    private CertificateIdentity Identity => new(_name.Text.Trim(), Email, Clean(_organization.Text), Clean(_unit.Text),
        Clean(_city.Text), Clean(_state.Text), Clean(_country.Text));

    private static string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private int Years => double.IsNaN(_years.Value) ? 0 : (int)Math.Round(_years.Value);

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
            int years = Years;
            await Task.Run(() => CertificateMaker.CreatePfx(id, password, years, path));
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
