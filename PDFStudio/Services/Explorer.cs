using System.Diagnostics;
using System.IO;

namespace PDFStudio.Services;

/// <summary>
/// Abre o Explorador de Arquivos numa pasta ou com um arquivo selecionado. Só aceita caminho completo, que exista e
/// sem aspas (as aspas poderiam fechar o argumento e virar outra opção do Explorador).
/// </summary>
public static class Explorer
{
    public static bool OpenFolder(string? folder)
    {
        if (!IsSafe(folder) || !Directory.Exists(folder)) return false;
        return Start($"\"{folder}\"");
    }

    /// <summary>Abre a pasta do arquivo com ele selecionado.</summary>
    public static bool ShowFile(string? file)
    {
        if (!IsSafe(file) || !File.Exists(file)) return false;
        return Start($"/select,\"{file}\"");
    }

    internal static bool IsSafe(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && path.IndexOf('"') < 0
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    private static bool Start(string args)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
