using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        _loading = false;
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
