using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Pages;

/// <summary>Configurações: tema e lista de recentes.</summary>
public sealed partial class SettingsPage : Page
{
    private static readonly ElementTheme[] Themes = [ElementTheme.Dark, ElementTheme.Light, ElementTheme.Default];
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeRadios.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, App.Settings.Theme));
        VersionText.Text = $"PDF Studio {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        AuthorBox.Text = App.Settings.CommentAuthor;
        UpdateWindowsStatus();
        _loading = false;
    }

    private static string? ExePath => Environment.ProcessPath;

    private void UpdateWindowsStatus(string? error = null)
    {
        bool on = WindowsIntegration.IsRegistered();
        RegisterButton.Content = on ? "Atualizar no Windows" : "Adicionar ao Windows";
        UnregisterButton.IsEnabled = on;
        DefaultAppButton.IsEnabled = on;
        WindowsStatusText.Text = error ?? (on
            ? "O PDF Studio aparece no \"Abrir com\" e no botão direito dos arquivos PDF (no Windows 11, em \"Mostrar mais opções\")."
            : "Ainda não adicionado. Adicione para poder escolher o PDF Studio como leitor padrão.");
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ExePath is not { } exe) throw new InvalidOperationException("Não deu para achar o programa.");
            WindowsIntegration.Register(exe);
            UpdateWindowsStatus();
        }
        catch (Exception ex) { UpdateWindowsStatus($"Não deu para adicionar: {ex.Message}"); }
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsIntegration.Unregister();
            UpdateWindowsStatus();
        }
        catch (Exception ex) { UpdateWindowsStatus($"Não deu para remover: {ex.Message}"); }
    }

    private async void DefaultApp_Click(object sender, RoutedEventArgs e)
    {
        // Abre Configurações > Aplicativos padrão já na página do PDF Studio (ou na lista geral, se não der).
        var page = new Uri($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(WindowsIntegration.AppName)}");
        if (!await Windows.System.Launcher.LaunchUriAsync(page))
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    private void AuthorBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string name = AuthorBox.Text.Trim();
        if (name == App.Settings.CommentAuthor) return;
        App.Settings.CommentAuthor = name;
        App.Settings.Save();
    }

    private void ThemeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeRadios.SelectedIndex < 0) return;
        App.Settings.Theme = Themes[ThemeRadios.SelectedIndex];
        App.Settings.Save();
        App.MainWindow!.ApplyTheme(App.Settings.Theme);
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        App.Recent.Clear(keepStarred: true);
        ClearedText.Text = "Lista de recentes limpa (os arquivos com estrela foram mantidos).";
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        App.Recent.Clear(keepStarred: false);
        ClearedText.Text = "Lista de recentes e estrelas limpa.";
    }
}
