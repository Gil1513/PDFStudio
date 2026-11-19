using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

/// <summary>Etapa 6.1: colocar e remover senha.</summary>
public sealed partial class ViewerPage
{
    /// <summary>
    /// Senha digitada para abrir o arquivo atual (null = abriu sem senha). Continua guardada depois de
    /// "Remover senha", porque Desfazer pode voltar a uma versão com senha.
    /// </summary>
    private string? _password;

    /// <summary>Mensagem para quem tenta alterar um PDF que tem senha.</summary>
    private const string ProtectedEditHint =
        "Para alterar este PDF, remova a senha antes (Proteger → Remover senha). Depois, se quiser, coloque-a de novo.";

    // ---------------- Colocar senha ----------------

    private async Task ProtectAsync()
    {
        if (_path is null || DisplayPath is not { } source) return;
        var state = await Task.Run(() => PdfPasswords.Inspect(source));
        if (await AskProtectionAsync(state) is not { } answer) return;

        string? output = answer.SaveCopy
            ? await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_path) + " - protegido")
            : _path;
        if (output is null) return;
        bool replacesOpenFile = string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase);

        string temp = Path.Combine(Path.GetTempPath(), "PDFStudio", $"senha-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            string? current = answer.Current ?? _password;
            await Task.Run(() => PdfPasswords.Protect(source, answer.Options, temp, current));
            if (replacesOpenFile) DiscardWork(); // as alterações não salvas já estão no arquivo protegido
            await Task.Run(() => File.Copy(temp, output, overwrite: true));
        }
        catch (Exception ex) when (ex is PdfPasswordException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível colocar a senha", ex.Message);
            return;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temporário */ }
        }

        string open = answer.Options.OpenPassword;
        string what = open.Length > 0 ? "Será pedida a senha para abrir." : "Abre sem senha, com as restrições escolhidas.";
        if (replacesOpenFile)
        {
            await OpenAsync(output, open);
            ShowStatus(InfoBarSeverity.Success, "Senha colocada", $"{what} Guarde a senha: sem ela não é possível recuperar o arquivo.");
        }
        else
        {
            ShowStatus(InfoBarSeverity.Success, "PDF protegido salvo", $"{output}. {what}", "Abrir o arquivo protegido",
                async () => await OpenAsync(output, open));
        }
    }

    private sealed record ProtectionAnswer(PasswordOptions Options, string? Current, bool SaveCopy);

    /// <summary>Diálogo: senha para abrir, restrições opcionais e (se o PDF já for protegido) a senha atual.</summary>
    private async Task<ProtectionAnswer?> AskProtectionAsync(PasswordState state)
    {
        PasswordBox Box(string header) => new() { Header = header, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var open = Box("Senha para abrir");
        var confirm = Box("Repita a senha");
        var limit = new CheckBox { Content = "Também limitar o que pode ser feito sem a senha de permissões" };
        var print = new CheckBox { Content = "Imprimir", IsChecked = true };
        var copy = new CheckBox { Content = "Copiar texto e imagens", IsChecked = true };
        var edit = new CheckBox { Content = "Editar e organizar páginas", IsChecked = false };
        var comment = new CheckBox { Content = "Comentar e preencher formulários", IsChecked = true };
        var owner = Box("Senha de permissões (diferente da de abrir)");
        var limits = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(28, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock { Text = "Sem a senha de permissões, quem abrir o PDF pode:", Opacity = 0.8 },
                print, copy, edit, comment, owner,
                new TextBlock
                {
                    Text = "Os leitores de PDF respeitam essas restrições, mas elas não são uma proteção total. A senha para abrir é a proteção de verdade.",
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                },
            },
        };
        limit.Checked += (_, _) => limits.Visibility = Visibility.Visible;
        limit.Unchecked += (_, _) => limits.Visibility = Visibility.Collapsed;

        var current = Box(state == PasswordState.Restricted ? "Senha de permissões atual" : "Senha atual (de permissões, se houver)");
        bool askCurrent = state == PasswordState.Restricted || (state == PasswordState.OpenPassword && _password is null);
        current.Visibility = askCurrent ? Visibility.Visible : Visibility.Collapsed;

        var where = new RadioButtons
        {
            Header = "Onde gravar",
            SelectedIndex = 0,
            Items =
            {
                new RadioButton { Content = "Proteger este arquivo (substitui o original)" },
                new RadioButton { Content = "Salvar uma cópia protegida…" },
            },
        };
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
                new TextBlock
                {
                    Text = state == PasswordState.None
                        ? "O PDF será gravado com criptografia AES de 128 bits."
                        : "Este PDF já tem proteção; ela será trocada pela nova. O PDF será gravado com criptografia AES de 128 bits.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                },
                current, open, confirm, limit, limits, where, error,
            },
        };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Colocar senha",
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Proteger",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });

        PasswordOptions? options = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            bool restrict = limit.IsChecked == true;
            string? problem = (open.Password, confirm.Password, owner.Password) switch
            {
                ("", _, _) when !restrict => "Digite a senha para abrir (ou marque a opção de limitar).",
                var (o, c, _) when o != c => "As duas senhas para abrir não são iguais.",
                (_, _, "") when restrict => "Digite a senha de permissões.",
                var (o, _, w) when restrict && o == w => "A senha de permissões precisa ser diferente da senha para abrir.",
                _ when restrict && print.IsChecked == true && copy.IsChecked == true && edit.IsChecked == true && comment.IsChecked == true
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
            options = restrict
                ? new PasswordOptions(open.Password, owner.Password, print.IsChecked == true, copy.IsChecked == true, edit.IsChecked == true, comment.IsChecked == true)
                : new PasswordOptions(open.Password);
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || options is null) return null;
        return new ProtectionAnswer(options, askCurrent && current.Password.Length > 0 ? current.Password : null, where.SelectedIndex == 1);
    }

    // ---------------- Remover senha ----------------

    private async Task RemovePasswordAsync()
    {
        if (_path is null || DisplayPath is not { } source) return;
        var state = await Task.Run(() => PdfPasswords.Inspect(source));
        if (state == PasswordState.None)
        {
            ShowStatus(InfoBarSeverity.Informational, "Este PDF não tem senha", "Não há nada para remover.");
            return;
        }

        // Primeiro tenta com a senha digitada ao abrir; se ela não bastar, pede a de permissões.
        string? password = _password;
        string? problem = null;
        while (true)
        {
            if (password is not null)
            {
                problem = await CanRemoveWithAsync(source, password);
                if (problem is null) break;
            }
            password = await AskRemovalPasswordAsync(state, problem);
            if (password is null) return;
        }

        string good = password;
        if (!await ApplyEditAsync((input, output) => PdfPasswords.Remove(input, good, output), allowProtected: true)) return;
        ShowStatus(InfoBarSeverity.Success, "Senha removida", "Salve (Ctrl+S) para gravar o PDF sem senha. Use Desfazer (Ctrl+Z) para voltar.");
    }

    /// <summary>null = a senha serve para tirar a proteção; senão, o motivo.</summary>
    private static Task<string?> CanRemoveWithAsync(string source, string password) => Task.Run(() =>
    {
        string temp = Path.Combine(Path.GetTempPath(), "PDFStudio", $"teste-senha-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            PdfPasswords.Remove(source, password, temp);
            return (string?)null;
        }
        catch (PdfPasswordException ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temporário */ }
        }
    });

    private async Task<string?> AskRemovalPasswordAsync(PasswordState state, string? problem)
    {
        var box = new PasswordBox { Header = state == PasswordState.Restricted ? "Senha de permissões" : "Senha", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = state == PasswordState.Restricted
                        ? "Este PDF abre sem senha, mas tem restrições. Para tirá-las, digite a senha de permissões."
                        : "Digite a senha do PDF. Se ele tiver também uma senha de permissões, use essa.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                },
                box,
            },
        };
        if (problem is not null)
            panel.Children.Add(new TextBlock
            {
                Text = problem,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
            });
        var dialog = Prepare(new ContentDialog
        {
            Title = "Remover senha",
            Content = panel,
            PrimaryButtonText = "Remover",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }
}
