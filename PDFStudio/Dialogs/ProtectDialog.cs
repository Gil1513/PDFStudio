using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>
/// Etapa 6.1 (separado na 8.2 para servir também ao lote): "Colocar senha". Senha para abrir, restrições opcionais
/// (com senha de permissões), a senha atual (se pedida) e, no visualizador, onde gravar.
/// </summary>
public sealed class ProtectDialog : ContentDialog
{
    private readonly PasswordBox _open, _confirm, _owner, _current;
    private readonly CheckBox _limit, _print, _copy, _edit, _comment;
    private readonly RadioButtons? _where;
    private readonly bool _askCurrent;

    /// <summary>As opções escolhidas (depois de "Proteger").</summary>
    public PasswordOptions? Options { get; private set; }

    /// <summary>A senha atual digitada (null = não pedida ou vazia).</summary>
    public string? CurrentPassword => _askCurrent && _current.Password.Length > 0 ? _current.Password : null;

    /// <summary>Escolheu "Salvar uma cópia protegida…" (só quando <c>showWhere</c>).</summary>
    public bool SaveCopy => _where?.SelectedIndex == 1;

    /// <param name="intro">Texto do alto.</param>
    /// <param name="currentHeader">Rótulo da caixa "senha atual" (null = não pede).</param>
    /// <param name="showWhere">Mostra "Proteger este arquivo / Salvar uma cópia".</param>
    /// <param name="primaryText">Texto do botão principal.</param>
    public ProtectDialog(string intro, string? currentHeader, bool showWhere, string primaryText = "Proteger")
    {
        PasswordBox Box(string header) => new() { Header = header, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        _open = Box("Senha para abrir");
        _confirm = Box("Repita a senha");
        _limit = new CheckBox { Content = "Também limitar o que pode ser feito sem a senha de permissões" };
        _print = new CheckBox { Content = "Imprimir", IsChecked = true };
        _copy = new CheckBox { Content = "Copiar texto e imagens", IsChecked = true };
        _edit = new CheckBox { Content = "Editar e organizar páginas", IsChecked = false };
        _comment = new CheckBox { Content = "Comentar e preencher formulários", IsChecked = true };
        _owner = Box("Senha de permissões (diferente da de abrir)");
        var limits = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(28, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock { Text = "Sem a senha de permissões, quem abrir o PDF pode:", Opacity = 0.8 },
                _print, _copy, _edit, _comment, _owner,
                new TextBlock
                {
                    Text = "Os leitores de PDF respeitam essas restrições, mas elas não são uma proteção total. A senha para abrir é a proteção de verdade.",
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                },
            },
        };
        _limit.Checked += (_, _) => limits.Visibility = Visibility.Visible;
        _limit.Unchecked += (_, _) => limits.Visibility = Visibility.Collapsed;

        _askCurrent = currentHeader is not null;
        _current = Box(currentHeader ?? "");
        _current.Visibility = _askCurrent ? Visibility.Visible : Visibility.Collapsed;

        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left },
                _current, _open, _confirm, _limit, limits,
            },
        };
        if (showWhere)
        {
            _where = new RadioButtons
            {
                Header = "Onde gravar",
                SelectedIndex = 0,
                Items =
                {
                    new RadioButton { Content = "Proteger este arquivo (substitui o original)" },
                    new RadioButton { Content = "Salvar uma cópia protegida…" },
                },
            };
            panel.Children.Add(_where);
        }
        panel.Children.Add(error);

        Title = "Colocar senha";
        Content = new ScrollViewer { Content = panel };
        PrimaryButtonText = primaryText;
        CloseButtonText = "Cancelar";
        DefaultButton = ContentDialogButton.Primary;

        PrimaryButtonClick += (_, args) =>
        {
            bool restrict = _limit.IsChecked == true;
            string? problem = (_open.Password, _confirm.Password, _owner.Password) switch
            {
                ("", _, _) when !restrict => "Digite a senha para abrir (ou marque a opção de limitar).",
                var (o, c, _) when o != c => "As duas senhas para abrir não são iguais.",
                (_, _, "") when restrict => "Digite a senha de permissões.",
                var (o, _, w) when restrict && o == w => "A senha de permissões precisa ser diferente da senha para abrir.",
                _ when restrict && _print.IsChecked == true && _copy.IsChecked == true && _edit.IsChecked == true && _comment.IsChecked == true
                    => "Desmarque pelo menos uma permissão (ou desmarque a opção de limitar).",
                _ => null,
            };
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
            Options = restrict
                ? new PasswordOptions(_open.Password, _owner.Password, _print.IsChecked == true, _copy.IsChecked == true, _edit.IsChecked == true, _comment.IsChecked == true)
                : new PasswordOptions(_open.Password);
        };
    }
}
