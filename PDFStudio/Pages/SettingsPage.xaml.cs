using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;

namespace PDFStudio.Pages;

/// <summary>Configurações: tema, nome nos comentários, abrir PDFs pelo Windows, recentes e sobre.</summary>
public sealed partial class SettingsPage : Page
{
    private static readonly ElementTheme[] Themes = [ElementTheme.Dark, ElementTheme.Light, ElementTheme.Default];
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        ShowTheme(Math.Max(0, Array.IndexOf(Themes, App.Settings.Theme)));
        VersionText.Text = $"PDF Studio {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        AuthorBox.Text = App.Settings.CommentAuthor;
        UpdateWindowsStatus();
        _loading = false;
    }

    // ---------- Aparência ----------

    private void ShowTheme(int index)
    {
        var tiles = new[] { ThemeTile0, ThemeTile1, ThemeTile2 };
        var checks = new[] { ThemeCheck0, ThemeCheck1, ThemeCheck2 };
        for (int i = 0; i < tiles.Length; i++)
        {
            bool on = i == index;
            tiles[i].BorderBrush = on ? (Brush)Resources["AppBlueBrush"] : (Brush)Application.Current.Resources["ToolCardBorderBrush"];
            checks[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ThemeTile_Click(object sender, RoutedEventArgs e)
    {
        int index = int.Parse((string)((Button)sender).Tag);
        ShowTheme(index);
        if (App.Settings.Theme == Themes[index]) return;
        App.Settings.Theme = Themes[index];
        App.Settings.Save();
        App.MainWindow!.ApplyTheme(App.Settings.Theme);
        ShowTheme(index); // a cor da borda muda junto com o tema
    }

    // ---------- Comentários ----------

    private void AuthorBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string name = AuthorBox.Text.Trim();
        if (name == App.Settings.CommentAuthor) return;
        App.Settings.CommentAuthor = name;
        App.Settings.Save();
    }

    // ---------- Abrir PDFs pelo Windows ----------

    private void UpdateWindowsStatus(string? error = null)
    {
        bool on = WindowsIntegration.IsRegistered();
        _loading = true;
        WindowsToggle.IsOn = on;
        _loading = false;
        DefaultAppButton.IsEnabled = on;
        WindowsStatusText.Text = error ?? (on
            ? "Ligado: o PDF Studio aparece em \"Abrir com\" e no botão direito dos PDFs (no Windows 11, em \"Mostrar mais opções\")."
            : "Coloca o PDF Studio em \"Abrir com\" e no botão direito dos PDFs. Vale só para o seu usuário.");
    }

    private void WindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (WindowsToggle.IsOn)
            {
                if (Environment.ProcessPath is not { } exe) throw new InvalidOperationException("não deu para achar o programa.");
                WindowsIntegration.Register(exe);
            }
            else WindowsIntegration.Unregister();
            UpdateWindowsStatus();
        }
        catch (Exception ex) { UpdateWindowsStatus($"Não deu certo: {ex.Message}"); }
    }

    private async void DefaultApp_Click(object sender, RoutedEventArgs e)
    {
        // Abre Configurações > Aplicativos padrão já na página do PDF Studio (ou na lista geral, se não der).
        var page = new Uri($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(WindowsIntegration.AppName)}");
        if (!await Windows.System.Launcher.LaunchUriAsync(page))
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    // ---------- Recentes ----------

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        App.Recent.Clear(keepStarred: true);
        ClearedText.Text = "Lista limpa. Os arquivos com estrela foram mantidos.";
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        App.Recent.Clear(keepStarred: false);
        ClearedText.Text = "Lista e estrelas limpas.";
    }
}
