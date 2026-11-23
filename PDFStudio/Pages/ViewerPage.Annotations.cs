using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Etapa 4: comentários. Edição numa cópia de trabalho (salvar, desfazer/refazer) e ferramentas de desenho.
public sealed partial class ViewerPage
{
    /// <summary>A única instância (a página fica em cache); usada pela janela para avisar sobre alterações não salvas.</summary>
    public static ViewerPage? Instance { get; private set; }

    private WorkingCopy? _work;

    /// <summary>Arquivo mostrado na tela: a versão em edição, se houver, senão o original.</summary>
    private string? DisplayPath => _work?.CurrentPath ?? _path;

    public bool HasUnsavedChanges => _work?.IsDirty == true || _editsRunning > 0 || HasPendingMarks;

    /// <summary>Desenho livre, caixa de texto ou bloco de texto que ainda não viraram alteração no arquivo.</summary>
    private bool HasPendingMarks => _inkStrokes.Count > 0 || _textEditor is not null || _blockEditor is not null;

    /// <summary>Grava no arquivo de trabalho o que ainda está pendente na tela (antes de salvar).</summary>
    private async Task CommitPendingMarksAsync()
    {
        if (_textEditor is not null) await FinishTextEditorAsync(commit: true);
        if (_blockEditor is not null) await FinishBlockEditorAsync(commit: true);
        if (_inkStrokes.Count > 0) await CommitInkAsync();
    }

    /// <summary>
    /// Uma alteração por vez: duas alterações seguidas (ex.: dois retângulos desenhados depressa) não podem
    /// partir da mesma versão, senão a primeira some. Salvar e Desfazer/Refazer também esperam a vez.
    /// </summary>
    private readonly SemaphoreSlim _editGate = new(1, 1);

    /// <summary>Alterações pedidas e ainda não terminadas (contam como "não salvo").</summary>
    private int _editsRunning;

    /// <summary>
    /// Muda sempre que o arquivo aberto é trocado ou fechado (DiscardWork). Uma alteração que termina depois
    /// disso é jogada fora em vez de ser aplicada ao arquivo novo.
    /// </summary>
    private int _docGeneration;
    private bool _signatureLossWarned; // já avisou que a alteração invalida a assinatura (uma vez por documento)

    // =====================================================================
    // Cópia de trabalho: aplicar, salvar, desfazer/refazer
    // =====================================================================

    /// <summary>
    /// Aplica uma alteração na cópia de trabalho (criada na primeira alteração) e redesenha as páginas.
    /// <paramref name="operation"/> recebe (versão atual, arquivo novo).
    /// </summary>
    /// <param name="allowProtected">A operação sabe lidar com PDF que tem senha (ex.: remover a senha).</param>
    /// <param name="label">Nome da alteração, mostrado em "Desfazer: …" (ex.: "Excluir páginas").</param>
    private async Task<bool> ApplyEditAsync(Action<string, string> operation, bool allowProtected = false, string? label = null)
    {
        if (_path is null) return false;
        int generation = _docGeneration;
        _editsRunning++;
        try
        {
            await _editGate.WaitAsync();
            try
            {
                return await ApplyEditCoreAsync(operation, allowProtected, label, generation);
            }
            finally
            {
                _editGate.Release();
            }
        }
        finally
        {
            _editsRunning--;
        }
    }

    private async Task<bool> ApplyEditCoreAsync(Action<string, string> operation, bool allowProtected, string? label, int generation)
    {
        // Enquanto esperava a vez, o arquivo pode ter sido trocado ou fechado: não aplica no arquivo errado.
        if (generation != _docGeneration || _path is null) return false;
        string path = _path;
        if (_password is not null && !allowProtected && DisplayPath is { } current
            && await Task.Run(() => { try { return PdfPasswords.Inspect(current); } catch (Exception) { return PasswordState.None; } })
                == PasswordState.OpenPassword)
        {
            if (generation == _docGeneration) ShowStatus(InfoBarSeverity.Warning, "Este PDF tem senha", ProtectedEditHint);
            return false;
        }
        if (generation != _docGeneration) return false;
        bool first = _work is null;
        try
        {
            if (_work is null)
            {
                var created = await Task.Run(() => new WorkingCopy(path));
                if (generation != _docGeneration) // trocou de arquivo enquanto a cópia era criada
                {
                    created.Dispose();
                    return false;
                }
                _work = created;
            }
            var work = _work;
            await Task.Run(() => work.Apply(operation, label));
        }
        catch (Exception ex)
        {
            if (generation != _docGeneration) return false;
            bool restricted = ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase);
            ShowStatus(InfoBarSeverity.Error, restricted ? "Este PDF tem restrições de edição" : "Não foi possível alterar o documento",
                restricted ? ProtectedEditHint : ex.Message);
            return false;
        }
        if (generation != _docGeneration) return false;

        if (_hideComments) ShowCommentsAgain(); // um comentário novo precisa aparecer
        await RefreshDocumentAsync();
        if (generation != _docGeneration) return false;
        UpdateEditState();
        if (first) _signatureLossWarned = false;
        if (await IsSignedAsync(path) && generation == _docGeneration && _work is { } workCopy)
        {
            // Comentários vão para o fim do arquivo sem tocar nos bytes assinados; outras alterações regravam tudo.
            bool kept = await Task.Run(() => DocumentPropertiesService.OnlyAppended(path, workCopy.CurrentPath));
            if (generation != _docGeneration) return true;
            if (!kept && !_signatureLossWarned)
            {
                _signatureLossWarned = true;
                ShowStatus(InfoBarSeverity.Warning, "Este PDF tem assinatura digital",
                    "Esta alteração invalida as assinaturas existentes ao salvar. Para manter o original assinado, use \"Salvar como\". " +
                    "(Comentários não invalidam: eles são gravados no fim do arquivo.)");
            }
            else if (kept && first)
                ShowStatus(InfoBarSeverity.Informational, "A assinatura continua válida",
                    "Comentários são gravados no fim do arquivo, sem mexer na parte assinada.");
        }
        return true;
    }

    /// <summary>Recarrega o arquivo mostrado e redesenha só as páginas e miniaturas que estão na tela.</summary>
    private async Task RefreshDocumentAsync()
    {
        ClearCommentSelection(); // o arquivo mudou (editar, desfazer…): a seleção pode não valer mais
        int generation = _docGeneration;
        try
        {
            if (await RenderSourceAsync() is not { } path) return;
            var doc = await PdfRenderer.LoadAsync(path, _password); // a senha não atrapalha versões já sem senha (ex.: depois de Desfazer)
            if (generation != _docGeneration) return; // outro arquivo foi aberto enquanto carregava
            _document = doc;
            if (PagesChanged(doc)) { RebuildPages(doc); return; } // excluir/inserir/redimensionar (ou desfazer isso)
            foreach (var slot in _slots)
            {
                if (slot.Image is not null)
                {
                    var image = await PdfRenderer.RenderAsync(doc, slot.Index, RenderScale);
                    if (doc == _document) slot.Image = image;
                }
                if (slot.Thumbnail is not null)
                {
                    var thumb = await PdfRenderer.RenderAsync(doc, slot.Index, ThumbnailScale(slot));
                    if (doc == _document) slot.Thumbnail = thumb;
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível atualizar a visualização", ex.Message);
        }
    }

    /// <summary>O documento novo tem outro número de páginas ou páginas de outro tamanho?</summary>
    private bool PagesChanged(Windows.Data.Pdf.PdfDocument doc)
    {
        if (doc.PageCount != _slots.Count) return true;
        for (int i = 0; i < _slots.Count; i++)
        {
            using var page = doc.GetPage((uint)i);
            if (Math.Abs(page.Size.Width - _slots[i].Width) > 0.5 || Math.Abs(page.Size.Height - _slots[i].Height) > 0.5) return true;
        }
        return false;
    }

    /// <summary>Remonta a lista de páginas (e miniaturas) e volta para a página em que o usuário estava.</summary>
    private void RebuildPages(Windows.Data.Pdf.PdfDocument doc, int? goToPage = null)
    {
        int page = goToPage ?? CurrentIndex;
        _slots = PdfRenderer.CreateSlots(doc);
        PagesRepeater.ItemsSource = _slots;
        ThumbnailList.ItemsSource = _slots;
        PageCountText.Text = $"de {_slots.Count}";
        PageNumberBox.Maximum = Math.Max(1, _slots.Count);
        int target = Math.Clamp(page, 0, Math.Max(0, _slots.Count - 1));
        SetPageBox(target + 1);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (target < _slots.Count) GoToPage(target); });
    }

    private static double ThumbnailScale(PageSlot slot) => PageSlot.ThumbMaxWidth * 1.5 / Math.Max(slot.Width, slot.Height);

    /// <summary>Atualiza botões Salvar/Desfazer/Refazer e o título (marca "não salvo").</summary>
    private void UpdateEditState()
    {
        UndoButton.IsEnabled = _work?.CanUndo == true;
        RedoButton.IsEnabled = _work?.CanRedo == true;
        // A dica diz o que vai ser desfeito/refeito (ex.: "Desfazer: Excluir páginas (Ctrl+Z)").
        string undo = _work?.UndoLabel is { } u ? $"Desfazer: {u}" : "Desfazer";
        string redo = _work?.RedoLabel is { } r ? $"Refazer: {r}" : "Refazer";
        ToolTipService.SetToolTip(UndoButton, undo + " (Ctrl+Z)");
        ToolTipService.SetToolTip(RedoButton, redo + " (Ctrl+Y)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UndoButton, undo);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RedoButton, redo);
        QuickSaveButton.IsEnabled = HasUnsavedChanges;
        if (_path is not null) App.MainWindow!.SetSubtitle(FileName!);
        _ = RefreshCommentListAsync();
        _ = RefreshAttachmentListAsync();
        _ = RefreshSearchAfterEditAsync();
        _ = RefreshBookmarksAsync();
    }

    /// <summary>Grava as alterações no arquivo original. Retorna false se não conseguiu.</summary>
    private async Task<bool> SaveAsync()
    {
        // Primeiro grava o que ainda está pendente na tela (traços, texto sendo digitado), depois espera
        // uma alteração que ainda está sendo gravada, para salvar com ela.
        await CommitPendingMarksAsync();
        await _editGate.WaitAsync();
        try
        {
            if (_work is not { IsDirty: true } work) return true;
            await Task.Run(work.Save);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar",
                $"{ex.Message} Se o arquivo estiver aberto em outro programa, feche-o ou use \"Salvar como\".");
            return false;
        }
        finally
        {
            _editGate.Release();
        }
        var saved = _work;
        UpdateEditState();
        if (saved is not null) ShowStatus(InfoBarSeverity.Success, "Alterações salvas", saved.OriginalPath);
        return true;
    }

    private async Task SaveAsAsync()
    {
        if (_path is null) return;
        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_path) + " - editado");
        if (output is null) return;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            await SaveAsync();
            return;
        }
        try
        {
            if (_work is { } work) await Task.Run(() => work.SaveAs(output));
            else { string source = _path; await Task.Run(() => File.Copy(source, output, overwrite: true)); }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar", ex.Message);
            return;
        }
        _path = output; // a partir de agora, o arquivo aberto é o novo
        App.Recent.Add(output);
        UpdateEditState();
        ShowStatus(InfoBarSeverity.Success, "Arquivo salvo", output);
    }

    private async Task UndoRedoAsync(bool undo)
    {
        if (_textEditor is not null) { await FinishTextEditorAsync(commit: false); return; }
        CancelDrawing();
        if (undo && _inkStrokes.Count > 0) // primeiro desfaz o desenho livre que ainda não foi gravado
        {
            ClearInk();
            return;
        }
        // Espera uma alteração em andamento terminar, para desfazer a versão certa.
        await _editGate.WaitAsync();
        try
        {
            if (_work is null || (undo ? !_work.CanUndo : !_work.CanRedo)) return;
            string? label = undo ? _work.UndoLabel : _work.RedoLabel;
            if (undo) _work.Undo(); else _work.Redo();
            await RefreshDocumentAsync();
            UpdateEditState();
            if (label is not null) ShowStatus(InfoBarSeverity.Informational, undo ? "Desfeito" : "Refeito", label);
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>Descarta a cópia de trabalho (sem perguntar).</summary>
    public void DiscardWork()
    {
        StopReadAloud(); // abrir/fechar outro arquivo encerra a leitura em voz alta
        _docGeneration++; // alterações ainda em andamento não valem mais para o arquivo novo
        _work?.Dispose();
        _work = null;
        ShowCommentsAgain();
        DeleteHiddenCopy();
        UpdateEditState();
    }

    /// <summary>
    /// Se houver alterações não salvas, pergunta: Salvar / Não salvar / Cancelar.
    /// Retorna true se pode continuar (salvou ou descartou).
    /// </summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasUnsavedChanges) return true;
        // Outra janela de diálogo já aberta: não dá para perguntar agora, então não descarta nada.
        if (DialogGuard.AnyOpen(App.MainWindow!.Content.XamlRoot)) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = App.MainWindow!.Content.XamlRoot,
            RequestedTheme = App.Settings.Theme,
            Title = "Salvar as alterações?",
            Content = $"\"{Path.GetFileName(_path)}\" tem alterações que ainda não foram salvas.",
            PrimaryButtonText = "Salvar",
            SecondaryButtonText = "Não salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    /// <summary>Para ferramentas que trabalham direto no arquivo: exige salvar as alterações antes.</summary>
    private async Task<bool> EnsureSavedAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = Prepare(new ContentDialog
        {
            Title = "Salvar antes de continuar?",
            Content = "Esta ferramenta trabalha com o arquivo salvo. Salve as alterações (comentários etc.) para continuar.",
            PrimaryButtonText = "Salvar e continuar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary && await SaveAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();
    private async void Undo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: true);
    private async void Redo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: false);

    private async void Accel_Save(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; await SaveAsync(); }

    private async void Accel_Undo(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (IsTyping()) return; // numa caixa de texto, Ctrl+Z desfaz a digitação
        a.Handled = true;
        await UndoRedoAsync(undo: true);
    }

    private async void Accel_Redo(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (IsTyping()) return;
        a.Handled = true;
        await UndoRedoAsync(undo: false);
    }

    private bool IsTyping() => FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or NumberBox;

    // =====================================================================
    // Ferramentas de comentário
    // =====================================================================

    /// <summary>Como a ferramenta usa o mouse.</summary>
    private enum DrawMode
    {
        /// <summary>Arrastar uma caixa (retângulo, oval).</summary>
        Box,
        /// <summary>Arrastar do início ao fim (linha, seta).</summary>
        Segment,
        /// <summary>Traço livre; vários traços viram um único desenho ao concluir.</summary>
        Freehand,
        /// <summary>Arrastar (ou clicar) e digitar o texto na própria página.</summary>
        Text,
        /// <summary>Clicar onde fica o ícone da nota e digitar o texto.</summary>
        Note,
        /// <summary>Arrastar sobre o texto da página, como numa seleção (realçar, sublinhar, riscar).</summary>
        TextSelect,
        /// <summary>Clicar onde o carimbo deve ficar (a prévia acompanha o mouse).</summary>
        Stamp,
        /// <summary>Etapa 5.1: clicar e digitar um texto que passa a fazer parte da página (não é comentário).</summary>
        PageText,
        /// <summary>Etapa 5.2: clicar (tamanho automático) ou arrastar (tamanho escolhido) para colocar uma imagem.</summary>
        Image,
        /// <summary>Etapa 5.3: arrastar sobre a área que vira link; clicar num link existente edita ou exclui.</summary>
        Link,
        /// <summary>Etapa 5.8: mostra os blocos da página (parágrafos e imagens); clicar seleciona um.</summary>
        EditAll,
        /// <summary>Etapa 6.2: arrastar marca uma área a redigir; clicar numa marca a desfaz. Aplica tudo de uma vez.</summary>
        Redact,
        /// <summary>Etapa 6.4: arrastar (ou clicar) cria um campo de assinatura; clicar num campo vazio oferece excluir.</summary>
        SignField,
        /// <summary>Clicar num comentário seleciona; arrastar move; os cantos mudam o tamanho.</summary>
        MoveComment,
    }

    /// <summary>Uma ferramenta de comentário: chave do botão, nome e o que mostrar na barra de opções.</summary>
    private sealed record CommentTool(string Key, string Name, AnnotationKind Kind, DrawMode Mode, string Hint,
        bool Arrow = false, bool UsesColor = true, bool UsesWidth = true, bool UsesFontSize = false);

    private static readonly Dictionary<string, CommentTool> CommentTools = new[]
    {
        new CommentTool("movercomentario", "Mover comentários", AnnotationKind.Other, DrawMode.MoveComment,
            "Clique num comentário para selecionar; arraste para mover e puxe os cantos para mudar o tamanho.",
            UsesColor: false, UsesWidth: false),
        new CommentTool("realcar", "Realçar", AnnotationKind.Highlight, DrawMode.TextSelect,
            "Arraste sobre o texto para realçar.", UsesWidth: false),
        new CommentTool("sublinhar", "Sublinhar", AnnotationKind.Underline, DrawMode.TextSelect,
            "Arraste sobre o texto para sublinhar.", UsesWidth: false),
        new CommentTool("riscar", "Riscar", AnnotationKind.StrikeOut, DrawMode.TextSelect,
            "Arraste sobre o texto para riscar.", UsesWidth: false),
        new CommentTool("retangulo", "Retângulo", AnnotationKind.Rectangle, DrawMode.Box, "Arraste na página para desenhar."),
        new CommentTool("oval", "Oval", AnnotationKind.Oval, DrawMode.Box, "Arraste na página para desenhar."),
        new CommentTool("linha", "Linha", AnnotationKind.Line, DrawMode.Segment, "Arraste do início ao fim da linha. Segure Shift para retas a cada 45°."),
        new CommentTool("seta", "Seta", AnnotationKind.Line, DrawMode.Segment, "Arraste do início até onde a seta aponta. Shift: ângulos de 45°.", Arrow: true),
        new CommentTool("desenho", "Desenho livre", AnnotationKind.Ink, DrawMode.Freehand,
            "Desenhe com o mouse; pode fazer vários traços na mesma página. Clique em Concluir ou troque de página para gravar."),
        new CommentTool("texto", "Caixa de texto", AnnotationKind.TextBox, DrawMode.Text,
            "Clique (ou arraste uma caixa) e digite. Clique fora ou Esc para concluir.", UsesWidth: false, UsesFontSize: true),
        new CommentTool("nota", "Nota", AnnotationKind.Note, DrawMode.Note,
            "Clique onde a nota deve ficar e digite o texto. Clique fora ou Esc para concluir.", UsesWidth: false),
        new CommentTool("carimbo", "Carimbo", AnnotationKind.Stamp, DrawMode.Stamp,
            "Clique na página onde o carimbo deve ficar.", UsesWidth: false),
        new CommentTool("adicionartexto", "Adicionar texto", AnnotationKind.Other, DrawMode.PageText,
            "Clique onde o texto deve começar e digite (Enter = nova linha). Clique fora ou Esc para concluir.",
            UsesWidth: false, UsesFontSize: true),
        new CommentTool("imagem", "Imagem", AnnotationKind.Other, DrawMode.Image,
            "Clique para colocar a imagem ou arraste para escolher o tamanho (a proporção é mantida).",
            UsesColor: false, UsesWidth: false),
        new CommentTool("link", "Link", AnnotationKind.Other, DrawMode.Link,
            "Arraste sobre a área que vai virar link (ex.: um texto). Clique num link existente (contorno azul) para editar ou excluir.",
            UsesColor: false, UsesWidth: false),
        new CommentTool("editartudo", "Editar tudo", AnnotationKind.Other, DrawMode.EditAll,
            "Textos (azul), imagens (verde) e desenhos, como linhas e caixas (laranja), aparecem contornados. Clique para selecionar; clique de novo num texto para editar. Arraste para mover; Delete apaga.",
            UsesColor: false, UsesWidth: false),
        new CommentTool("redigir", "Redigir", AnnotationKind.Other, DrawMode.Redact,
            "Arraste sobre o que deve sumir (pode marcar várias áreas, em várias páginas). Depois clique em Aplicar redação.",
            UsesColor: false, UsesWidth: false),
        new CommentTool("campoassinatura", "Campo de assinatura", AnnotationKind.Other, DrawMode.SignField,
            "Arraste (ou clique) onde a pessoa deve assinar. Clique num campo vazio para excluí-lo.",
            UsesColor: false, UsesWidth: false),
    }.ToDictionary(t => t.Key);

    private static readonly (string Name, string Hex)[] Palette =
    [
        ("Vermelho", "#E53935"), ("Laranja", "#FB8C00"), ("Amarelo", "#FDD835"), ("Verde", "#43A047"),
        ("Azul", "#1E88E5"), ("Roxo", "#8E24AA"), ("Preto", "#212121"),
    ];

    private static readonly double[] LineWidths = [1, 2, 3, 5, 8];
    private static readonly double[] FontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72];

    /// <summary>Modelos de carimbo (desenho próprio do PDF Studio). O último é o personalizado (texto e cor livres).</summary>
    private static readonly (string Label, string Text, string Hex, string Name, bool AlwaysDetail)[] StampPresets =
    [
        ("Aprovado", "Aprovado", "#2E7D32", "Approved", false),
        ("Rejeitado", "Rejeitado", "#C62828", "NotApproved", false),
        ("Confidencial", "Confidencial", "#C62828", "Confidential", false),
        ("Rascunho", "Rascunho", "#546E7A", "Draft", false),
        ("Recebido (com data)", "Recebido", "#1565C0", "Received", true),
        ("Personalizado…", "", "", "Custom", false),
    ];

    private bool IsCustomStamp => App.Settings.StampPreset == StampPresets.Length - 1;

    private CommentTool? _tool;

    private bool UsesHighlightColor => _tool?.Kind == AnnotationKind.Highlight;
    private bool IsPageTextTool => _tool?.Mode == DrawMode.PageText;

    /// <summary>Cor da ferramenta ativa (o realce e o "Adicionar texto" guardam a sua separada).</summary>
    private string ToolColorHex
    {
        get => UsesHighlightColor ? App.Settings.HighlightColor : IsPageTextTool ? App.Settings.PageTextColor : App.Settings.CommentColor;
        set
        {
            if (UsesHighlightColor) App.Settings.HighlightColor = value;
            else if (IsPageTextTool) App.Settings.PageTextColor = value;
            else App.Settings.CommentColor = value;
            App.Settings.Save();
        }
    }

    /// <summary>Tamanho da letra da ferramenta ativa (caixa de texto ou "Adicionar texto").</summary>
    private double ToolFontSize
    {
        get => IsPageTextTool ? App.Settings.PageTextFontSize : App.Settings.CommentFontSize;
        set
        {
            if (IsPageTextTool) App.Settings.PageTextFontSize = value; else App.Settings.CommentFontSize = value;
            App.Settings.Save();
        }
    }

    private AnnotationColor ToolColor => AnnotationColor.FromHex(ToolColorHex);
    private AnnotationStyle ToolStyle => new(ToolColor, App.Settings.CommentLineWidth);

    /// <summary>Monta a paleta de cores e a lista de espessuras (chamado no construtor).</summary>
    private void InitCommentOptions()
    {
        Instance = this;
        foreach (var (name, hex) in Palette)
        {
            var button = new Button
            {
                Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), Tag = hex,
                Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush(hex) },
            };
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += (s, _) =>
            {
                ToolColorHex = (string)((FrameworkElement)s).Tag;
                ColorSwatch.Background = Brush(ToolColorHex);
                ColorFlyout.Hide();
            };
            ColorPalette.Children.Add(button);
        }
        ColorSwatch.Background = Brush(App.Settings.CommentColor);

        foreach (double w in LineWidths) WidthBox.Items.Add($"{w:0} pt");
        int index = Array.IndexOf(LineWidths, App.Settings.CommentLineWidth);
        WidthBox.SelectedIndex = index >= 0 ? index : 1;

        foreach (var preset in StampPresets) StampBox.Items.Add(preset.Label);
        StampBox.SelectedIndex = Math.Clamp(App.Settings.StampPreset, 0, StampPresets.Length - 1);
        StampTextBox.Text = App.Settings.StampCustomText;
        StampDetailCheck.IsChecked = App.Settings.StampWithDetail;

        foreach (double size in FontSizes) FontSizeBox.Items.Add($"{size:0} pt");
        SelectToolFontSize();
    }

    private bool _settingFontSize;

    /// <summary>Mostra na lista o tamanho guardado da ferramenta ativa.</summary>
    private void SelectToolFontSize()
    {
        int index = Array.IndexOf(FontSizes, ToolFontSize);
        _settingFontSize = true;
        FontSizeBox.SelectedIndex = index >= 0 ? index : Array.IndexOf(FontSizes, 12);
        _settingFontSize = false;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var c = AnnotationColor.FromHex(hex);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
    }

    private void WidthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthBox.SelectedIndex < 0) return;
        App.Settings.CommentLineWidth = LineWidths[WidthBox.SelectedIndex];
        App.Settings.Save();
    }

    private void StampBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StampBox is null || StampBox.SelectedIndex < 0) return;
        App.Settings.StampPreset = StampBox.SelectedIndex;
        App.Settings.Save();
        UpdateStampOptions();
    }

    private void StampTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StampTextBox is null) return;
        App.Settings.StampCustomText = StampTextBox.Text;
        App.Settings.Save();
    }

    private void StampDetailCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.StampWithDetail = StampDetailCheck.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>No carimbo personalizado aparecem o campo de texto e a cor; nos modelos, a cor é a do modelo.</summary>
    private void UpdateStampOptions()
    {
        if (StampTextBox is null || _tool?.Mode != DrawMode.Stamp) return;
        StampTextBox.Visibility = IsCustomStamp ? Visibility.Visible : Visibility.Collapsed;
        ColorOption.Visibility = IsCustomStamp ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>O carimbo escolhido, com a linha de data e autor quando pedida (ou sempre, no "Recebido").</summary>
    private StampDesign? CurrentStamp()
    {
        var preset = StampPresets[Math.Clamp(App.Settings.StampPreset, 0, StampPresets.Length - 1)];
        string text = IsCustomStamp ? App.Settings.StampCustomText : preset.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var color = IsCustomStamp ? AnnotationColor.FromHex(App.Settings.CommentColor) : AnnotationColor.FromHex(preset.Hex);
        string? detail = null;
        if (preset.AlwaysDetail || App.Settings.StampWithDetail)
        {
            string author = App.Settings.CommentAuthor.Trim();
            string date = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
            detail = author.Length > 0 ? $"{author} · {date}" : date;
        }
        return new StampDesign(text, color, detail, preset.Name);
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontSizeBox is null || FontSizeBox.SelectedIndex < 0 || _settingFontSize) return;
        ToolFontSize = FontSizes[FontSizeBox.SelectedIndex];
    }

    /// <summary>
    /// Liga uma ferramenta (ou desliga, com null). A ferramenta continua ativa até Esc ou "Concluir".
    /// Traços pendentes do desenho livre são gravados (ou descartados, com <paramref name="keepPendingInk"/> = false).
    /// </summary>
    private void SetTool(string? key, bool keepPendingInk = true)
    {
        if (_textEditor is not null) _ = FinishTextEditorAsync(commit: keepPendingInk);
        ClearCopySelection();
        RemoveStampGhost();
        RemoveLinkOutlines();
        if (_blockEditor is not null) _ = FinishBlockEditorAsync(commit: keepPendingInk);
        RemoveBlockOutlines();
        ClearRedactMarks();
        ClearCommentSelection();
        ToolTipService.SetToolTip(ToolHintText, null);
        CancelImageDrag();
        RemoveImageGhost();
        CancelDrawing();
        if (_inkStrokes.Count > 0)
        {
            if (keepPendingInk) _ = CommitInkAsync();
            else ClearInk();
        }
        _tool = key is not null && CommentTools.TryGetValue(key, out var tool) ? tool : null;
        ToolOptionsBar.Visibility = _tool is null ? Visibility.Collapsed : Visibility.Visible;
        if (_tool is not null)
        {
            ToolNameText.Text = _tool.Name;
            ToolHintText.Text = _tool.Hint;
            ColorOption.Visibility = _tool.UsesColor ? Visibility.Visible : Visibility.Collapsed;
            WidthOption.Visibility = _tool.UsesWidth ? Visibility.Visible : Visibility.Collapsed;
            FontSizeOption.Visibility = _tool.UsesFontSize ? Visibility.Visible : Visibility.Collapsed;
            if (_tool.UsesFontSize) SelectToolFontSize();
            PageFontOption.Visibility = IsPageTextTool ? Visibility.Visible : Visibility.Collapsed;
            if (IsPageTextTool) InitPageFontOptions();
            ColorSwatch.Background = Brush(ToolColorHex);
            StampOption.Visibility = _tool.Mode == DrawMode.Stamp ? Visibility.Visible : Visibility.Collapsed;
            ImageOption.Visibility = _tool.Mode == DrawMode.Image ? Visibility.Visible : Visibility.Collapsed;
            RedactApplyButton.Visibility = _tool.Mode == DrawMode.Redact ? Visibility.Visible : Visibility.Collapsed;
            UpdateRedactHint();
            UpdateStampOptions();
            // Seleção de texto: já lê as palavras das páginas na tela, para a primeira seleção ser imediata.
            if (_tool.Mode == DrawMode.MoveComment && DisplayPath is not null) _ = CommentsForMove(); // já lê os comentários
            if (_tool.Mode == DrawMode.TextSelect && DisplayPath is not null)
                foreach (var slot in _slots.Where(s => s.Image is not null)) _ = WordsOfPage(slot.Index);
        }
        BuildToolbar(SelectedCategory);
    }

    private void FinishTool_Click(object sender, RoutedEventArgs e) => SetTool(null);

    // ---------------- Desenho com o mouse ----------------

    private DrawingSurface? _drawSurface;
    private PageSlot? _drawSlot;
    private Point _drawStart;
    private Shapes.Shape? _preview;

    // Desenho livre: traços já feitos (ainda não gravados) e os desenhos provisórios na tela.
    private readonly List<List<Point>> _inkStrokes = [];
    private readonly List<Shapes.Polyline> _inkPreviews = [];
    private DrawingSurface? _inkSurface;
    private PageSlot? _inkSlot;

    /// <summary>O visualizador mede em DIPs (1/96 pol.); o PDF, em pontos (1/72 pol.).</summary>
    private const double DipsPerPoint = 96.0 / 72;

    private void Surface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var surface = (DrawingSurface)sender;
        _plainCursor = null;
        surface.SetCursor(_tool is null ? null
            : _tool.Mode is DrawMode.TextSelect or DrawMode.PageText ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Cross);
        if (_tool?.Mode == DrawMode.Link) _ = ShowLinkOutlinesAsync(surface);
        else if (_tool?.Mode == DrawMode.EditAll) { surface.SetCursor(null); _ = ShowBlockOutlinesAsync(surface); }
        else if (_tool?.Mode == DrawMode.Redact) ShowRedactMarks(surface);
        else if (_tool?.Mode == DrawMode.MoveComment) { surface.SetCursor(null); if (DisplayPath is not null) _ = CommentsForMove(); }
        // Já lê os links e as palavras da página, para a "mãozinha" e a seleção de texto não terem atraso.
        else if (_tool is null && DisplayPath is not null && SlotOf(surface) is { } slot)
        {
            _ = LinksOfPage(slot.Index);
            _ = WordsOfPage(slot.Index);
        }
    }

    private SolidColorBrush ToolBrush()
    {
        var c = ToolColor;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
    }

    private async void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not DrawingSurface surface || SlotOf(surface) is not { } slot) return;
        var point = e.GetCurrentPoint(surface);
        if (point.Properties.IsRightButtonPressed && _tool is null)
        {
            // Botão direito: menu do texto (em cima de palavras) ou da página.
            e.Handled = true;
            await ShowContextMenuAsync(surface, slot, point.Position);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_tool is null)
        {
            // Sem ferramenta: clicar num link segue o link; arrastar sobre o texto seleciona (para copiar).
            e.Handled = true;
            if (LinkAt(slot, point.Position) is not null) { ClearCopySelection(); await FollowLinkAsync(slot, point.Position); }
            else StartCopySelection(surface, slot, e);
            return;
        }
        e.Handled = true;

        // Com um texto sendo digitado, clicar fora dele só conclui o texto.
        if (_textEditor is not null)
        {
            await FinishTextEditorAsync(commit: true);
            return;
        }
        if (_tool.Mode == DrawMode.TextSelect)
        {
            await StartTextSelectionAsync(surface, slot, e);
            return;
        }
        if (_tool.Mode == DrawMode.MoveComment)
        {
            await MovePressedAsync(surface, slot, e);
            return;
        }
        if (_tool.Mode == DrawMode.Image)
        {
            CancelDrawing();
            StartImageDrag(surface, slot, e);
            return;
        }
        if (_tool.Mode == DrawMode.EditAll)
        {
            if (_blockEditor is not null) { await FinishBlockEditorAsync(commit: true); return; } // clicar fora aplica
            surface.CapturePointer(e.Pointer); // antes de esperar: o bloco de texto pode ser arrastado
            _blockPressPending = true;
            _blockReleasedEarly = null;
            try
            {
                await BlocksOfPage(slot.Index); // a leitura pode ter começado agora há pouco (mouse chegando)
                await ShowBlockOutlinesAsync(surface);
            }
            finally { _blockPressPending = false; }
            await PressBlockAsync(surface, slot, point.Position);
            // Clique rápido: o botão já foi solto enquanto a página era lida. Termina o clique agora (senão o app
            // ficaria "arrastando" sozinho, com a moldura seguindo o mouse).
            if (_blockReleasedEarly is { } released)
            {
                _blockReleasedEarly = null;
                if (BlockGestureActive) await DropBlockAsync(surface, released);
            }
            return;
        }
        if (_tool.Mode == DrawMode.Redact)
        {
            ShowRedactMarks(surface);
            if (RedactMarkAt(slot, point.Position) is { } mark) { CancelDrawing(); RemoveRedactMark(mark); return; }
        }
        if (_tool.Mode == DrawMode.SignField && await ClickSignFieldAsync(slot, point.Position))
        {
            CancelDrawing();
            return;
        }
        if (_tool.Mode == DrawMode.Link && LinkAt(slot, point.Position) is { } existing)
        {
            CancelDrawing();
            await EditLinkAsync(surface, existing);
            return;
        }
        if (_tool.Mode == DrawMode.Stamp)
        {
            CancelDrawing();
            await PlaceStampAsync(slot, point.Position);
            return;
        }
        if (_tool.Mode == DrawMode.PageText)
        {
            CancelDrawing();
            OpenTextEditor(surface, slot, point.Position.X, point.Position.Y, 0, 0);
            return;
        }
        if (_tool.Mode == DrawMode.Note)
        {
            CancelDrawing();
            var p = point.Position;
            OpenTextEditor(surface, slot, p.X, p.Y, PdfAnnotations.NoteIconSize * DipsPerPoint, PdfAnnotations.NoteIconSize * DipsPerPoint);
            return;
        }

        // Desenho livre em outra página: grava o desenho da página anterior antes de começar.
        if (_tool.Mode == DrawMode.Freehand && _inkSlot is not null && _inkSlot != slot) await CommitInkAsync();

        CancelDrawing();
        surface.CapturePointer(e.Pointer);
        _drawSurface = surface;
        _drawSlot = slot;
        _drawStart = Clamp(point.Position);
        double thickness = App.Settings.CommentLineWidth * DipsPerPoint;

        switch (_tool.Mode)
        {
            case DrawMode.Box:
                _preview = _tool.Kind == AnnotationKind.Oval ? new Shapes.Ellipse() : new Shapes.Rectangle();
                PlaceBox(_drawStart);
                break;
            case DrawMode.Text or DrawMode.Link or DrawMode.Redact or DrawMode.SignField:
                _preview = new Shapes.Rectangle { StrokeDashArray = [4, 3] };
                thickness = 1;
                PlaceBox(_drawStart);
                break;
            case DrawMode.Segment:
                _preview = new Shapes.Line
                {
                    X1 = _drawStart.X, Y1 = _drawStart.Y, X2 = _drawStart.X, Y2 = _drawStart.Y,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                break;
            case DrawMode.Freehand:
                _inkSurface = surface;
                _inkSlot = slot;
                _inkStrokes.Add([_drawStart]);
                var line = new Shapes.Polyline
                {
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                line.Points.Add(_drawStart);
                _inkPreviews.Add(line);
                _preview = line;
                break;
        }
        _preview!.Stroke = _tool.Mode switch
        {
            DrawMode.Link or DrawMode.SignField => (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            DrawMode.Redact => RedactStroke,
            _ => ToolBrush(),
        };
        _preview.StrokeThickness = thickness;
        surface.Children.Add(_preview);
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_tool?.Mode == DrawMode.MoveComment && sender is DrawingSurface moveSurface)
        {
            MoveMoved(moveSurface, e);
            return;
        }
        if (_tool?.Mode == DrawMode.Image && sender is DrawingSurface imageSurface)
        {
            MoveImageGhost(imageSurface, e);
            return;
        }
        if (_tool?.Mode == DrawMode.EditAll && sender is DrawingSurface blockSurface)
        {
            if (BlockGestureActive) { e.Handled = true; DragBlock(blockSurface, e.GetCurrentPoint(blockSurface).Position); }
            else HoverBlock(blockSurface, e.GetCurrentPoint(blockSurface).Position);
            return;
        }
        if (_tool is null && sender is DrawingSurface plain)
        {
            if (_copyDragging && ReferenceEquals(plain, _copySurface)) MoveCopySelection(e.GetCurrentPoint(plain).Position);
            else UpdateLinkCursor(plain, e.GetCurrentPoint(plain).Position);
            return;
        }
        if (_tool?.Mode == DrawMode.Stamp && sender is DrawingSurface hovered)
        {
            MoveStampGhost(hovered, e.GetCurrentPoint(hovered).Position);
            return;
        }
        if (_tool?.Mode == DrawMode.TextSelect && _selLayer is not null && ReferenceEquals(sender, _drawSurface))
        {
            e.Handled = true;
            ShowSelection(Clamp(e.GetCurrentPoint(_drawSurface).Position));
            return;
        }
        if (_preview is null || _tool is null || !ReferenceEquals(sender, _drawSurface)) return;
        e.Handled = true;
        var current = Clamp(e.GetCurrentPoint(_drawSurface).Position);
        switch (_tool.Mode)
        {
            case DrawMode.Box or DrawMode.Text or DrawMode.Link or DrawMode.Redact or DrawMode.SignField:
                PlaceBox(current);
                break;
            case DrawMode.Segment when _preview is Shapes.Line line:
                var end = SnapAngle(current, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
                line.X2 = end.X;
                line.Y2 = end.Y;
                break;
            case DrawMode.Freehand when _preview is Shapes.Polyline poly:
                var last = poly.Points[^1];
                if (Math.Abs(current.X - last.X) + Math.Abs(current.Y - last.Y) < 1.5) break; // ignora tremidas mínimas
                poly.Points.Add(current);
                _inkStrokes[^1].Add(current);
                break;
        }
    }

    private async void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_tool?.Mode == DrawMode.EditAll && sender is DrawingSurface blockSurface)
        {
            var position = e.GetCurrentPoint(blockSurface).Position;
            _releasingBlockPointer = true; // soltar o mouse de propósito não é "perder" o mouse
            try { blockSurface.ReleasePointerCaptures(); } finally { _releasingBlockPointer = false; }
            if (_blockPressPending) { _blockReleasedEarly = position; e.Handled = true; return; } // o aperto ainda está sendo tratado
            if (BlockGestureActive) { e.Handled = true; await DropBlockAsync(blockSurface, position); }
            return;
        }
        if (_tool?.Mode == DrawMode.MoveComment && sender is DrawingSurface moveSurface)
        {
            e.Handled = true;
            await MoveReleasedAsync(moveSurface);
            return;
        }
        if (_tool is null && _copyDragging && sender is DrawingSurface copySurface && ReferenceEquals(copySurface, _copySurface))
        {
            e.Handled = true;
            await FinishCopySelectionAsync(e.GetCurrentPoint(copySurface).Position);
            return;
        }
        if (_tool?.Mode == DrawMode.Image && sender is DrawingSurface imageSurface)
        {
            await FinishImageDragAsync(imageSurface, e);
            return;
        }
        if (_tool?.Mode == DrawMode.TextSelect && _selLayer is not null && ReferenceEquals(sender, _drawSurface))
        {
            e.Handled = true;
            await FinishTextSelectionAsync(Clamp(e.GetCurrentPoint(_drawSurface).Position));
            return;
        }
        if (_preview is null || _tool is null || _drawSurface is null || _drawSlot is null || !ReferenceEquals(sender, _drawSurface)) return;
        e.Handled = true;
        var tool = _tool;
        int page = _drawSlot.Index;
        var current = Clamp(e.GetCurrentPoint(_drawSurface).Position);
        var style = ToolStyle;
        string author = App.Settings.CommentAuthor;

        if (tool.Mode == DrawMode.Freehand)
        {
            // O traço fica na tela; todos os traços viram um só comentário ao concluir.
            _drawSurface.ReleasePointerCaptures();
            _preview = null;
            _drawSurface = null;
            _drawSlot = null;
            ToolHintText.Text = "Clique em Concluir (ou Esc) para gravar o desenho.";
            return;
        }

        if (tool.Mode == DrawMode.Text)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            var surface = _drawSurface;
            var slot = _drawSlot;
            CancelDrawing();
            if (w < 30 || h < 10) (x, y, w, h) = (_drawStart.X, _drawStart.Y, 0, 0); // só um clique: tamanho padrão
            OpenTextEditor(surface, slot, x, y, w, h);
            return;
        }

        if (tool.Mode == DrawMode.Redact)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            var surface = _drawSurface;
            var slot = _drawSlot;
            CancelDrawing();
            if (w >= 4 && h >= 4) AddRedactMark(surface, slot, new Rect(x, y, w, h));
            return;
        }

        if (tool.Mode == DrawMode.SignField)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            var surface = _drawSurface;
            var slot = _drawSlot;
            CancelDrawing();
            await CreateSignFieldAsync(surface, slot, new Rect(x, y, w, h));
            return;
        }

        if (tool.Mode == DrawMode.Link)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            var surface = _drawSurface;
            var slot = _drawSlot;
            CancelDrawing();
            if (w < 6 || h < 6) return; // clique sem arrastar (fora de um link)
            await CreateLinkAsync(surface, slot, new Rect(x, y, w, h));
            return;
        }

        if (tool.Mode == DrawMode.Box)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            CancelDrawing();
            if (w < 4 || h < 4) return; // clique sem arrastar
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddShape(input, page, tool.Kind, x / DipsPerPoint, y / DipsPerPoint, w / DipsPerPoint, h / DipsPerPoint,
                    style, author, output), label: "Adicionar forma");
        }
        else
        {
            var start = _drawStart;
            var end = SnapAngle(current, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
            CancelDrawing();
            if (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) < 6) return;
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddLine(input, page, start.X / DipsPerPoint, start.Y / DipsPerPoint, end.X / DipsPerPoint, end.Y / DipsPerPoint,
                    tool.Arrow, style, author, output), label: "Adicionar linha");
        }
    }

    private bool _releasingBlockPointer;
    // Aperto no Editar tudo ainda lendo a página (espera) e, se o botão foi solto nesse meio-tempo, onde.
    private bool _blockPressPending;
    private Point? _blockReleasedEarly;

    private void Surface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_releasingBlockPointer && sender is DrawingSurface blockSurface && BlockGestureActive) BlockGestureLost(blockSurface);
        if (sender is DrawingSurface moveSurface && ReferenceEquals(moveSurface, _moveSurface)) MoveCaptureLost(moveSurface);
        if (ReferenceEquals(sender, _imageDragSurface)) { CancelImageDrag(); return; }
        // No desenho livre o traço já está guardado; nas outras ferramentas, perder o mouse cancela.
        // Na seleção de texto, soltar o botão também solta o mouse, mas a seleção termina em PointerReleased
        // (que pode ainda estar esperando a leitura das palavras).
        if (ReferenceEquals(sender, _drawSurface) && _tool?.Mode is not (DrawMode.Freehand or DrawMode.TextSelect)) CancelDrawing();
    }

    // ---------------- Caixa de texto e nota: digitar na página ----------------

    private TextBox? _textEditor;
    private DrawingSurface? _textSurface;
    private PageSlot? _textSlot;
    private Rect _textBox; // caixa de texto: posição e tamanho (DIPs); nota: posição e tamanho do ícone
    private DrawMode _textMode;

    /// <summary>Largura padrão (pontos) da caixa de texto criada com um clique e da área para digitar a nota.</summary>
    private const double DefaultTextWidth = 200, NoteEditorWidth = 220;

    /// <summary>Mostra uma caixa para digitar na página, na posição (x, y) em DIPs.</summary>
    private void OpenTextEditor(DrawingSurface surface, PageSlot slot, double x, double y, double width, double height)
    {
        _textMode = _tool?.Mode ?? DrawMode.Text;
        bool note = _textMode == DrawMode.Note;
        double fontSize = App.Settings.CommentFontSize;
        if (_textMode == DrawMode.Text && width <= 0)
        {
            width = Math.Min(DefaultTextWidth * DipsPerPoint, surface.ActualWidth - x);
            height = PdfAnnotations.TextHeight(1, fontSize) * DipsPerPoint;
        }
        _textBox = new Rect(x, y, width, height);
        _textSurface = surface;
        _textSlot = slot;

        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(0),
            RequestedTheme = ElementTheme.Light, // está sobre a página branca
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor,
            note ? "Texto da nota" : _textMode == DrawMode.PageText ? "Texto a escrever na página" : "Texto da caixa");
        if (_textMode == DrawMode.PageText)
        {
            // Texto da página: cresce conforme se digita, com a fonte, o tamanho, a cor e o estilo escolhidos.
            // O canto do texto fica no ponto clicado (a margem interna da caixa é descontada ao gravar).
            double pad = PageTextPadding * DipsPerPoint;
            editor.TextWrapping = TextWrapping.NoWrap;
            editor.MinWidth = 60;
            editor.FontFamily = new FontFamily(App.Settings.PageTextFont);
            editor.FontSize = App.Settings.PageTextFontSize * DipsPerPoint;
            editor.FontWeight = App.Settings.PageTextBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            editor.FontStyle = App.Settings.PageTextItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
            editor.Foreground = ToolBrush();
            editor.Padding = new Thickness(pad - 1);
            editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255));
            editor.PlaceholderText = "Digite aqui";
            _textBox = new Rect(x - pad, y - pad, 0, 0);
            Canvas.SetLeft(editor, x - pad);
            Canvas.SetTop(editor, y - pad);
        }
        else if (note)
        {
            // A nota: área de digitação ao lado do ponto clicado, com fundo de "post-it".
            editor.PlaceholderText = "Texto da nota";
            editor.Width = Math.Min(NoteEditorWidth * DipsPerPoint, Math.Max(120, surface.ActualWidth - 8));
            editor.MinHeight = 80;
            editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 249, 196));
            Canvas.SetLeft(editor, Math.Max(0, Math.Min(x + 4, surface.ActualWidth - editor.Width)));
            Canvas.SetTop(editor, Math.Max(0, y + 4));
            var icon = new FontIcon { Glyph = "\uE70B", FontSize = 18, Foreground = ToolBrush(), Tag = "note-icon" };
            Canvas.SetLeft(icon, x);
            Canvas.SetTop(icon, y);
            surface.Children.Add(icon);
        }
        else
        {
            // A caixa de texto: mesma posição, largura, letra e cor que terá no PDF.
            editor.Width = width;
            editor.MinHeight = height;
            editor.FontFamily = new FontFamily("Arial");
            editor.FontSize = fontSize * DipsPerPoint;
            editor.Foreground = ToolBrush();
            editor.Padding = new Thickness(PdfAnnotations.TextPadding * DipsPerPoint - 1);
            editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255));
            Canvas.SetLeft(editor, x);
            Canvas.SetTop(editor, y);
        }
        editor.LostFocus += async (_, _) => { if (ReferenceEquals(editor, _textEditor)) await FinishTextEditorAsync(commit: true); };
        _textEditor = editor;
        surface.Children.Add(editor);
        ToolHintText.Text = "Digite o texto. Clique fora (ou Esc) para concluir.";
        editor.Loaded += (_, _) => editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Fecha a caixa de digitação; com <paramref name="commit"/>, grava o texto (se houver) como comentário.</summary>
    private async Task FinishTextEditorAsync(bool commit)
    {
        if (_textEditor is not { } editor || _textSurface is not { } surface || _textSlot is not { } slot) return;
        _textEditor = null; // evita gravar duas vezes (perda de foco + clique)
        string text = editor.Text;
        bool note = _textMode == DrawMode.Note;
        var box = _textBox;
        surface.Children.Remove(editor);
        foreach (var icon in surface.Children.OfType<FontIcon>().Where(i => (i.Tag as string) == "note-icon").ToList())
            surface.Children.Remove(icon);
        _textSurface = null;
        _textSlot = null;
        if (_tool is not null) ToolHintText.Text = _tool.Hint;
        if (!commit || string.IsNullOrWhiteSpace(text)) return;

        int page = slot.Index;
        string author = App.Settings.CommentAuthor;
        var color = ToolColor;
        if (_textMode == DrawMode.PageText)
        {
            var style = CurrentPageTextStyle();
            double pad = PageTextPadding;
            await ApplyEditAsync((input, output) =>
                PdfPageContent.AddText(input, page, box.X / DipsPerPoint + pad, box.Y / DipsPerPoint + pad, text, style, output), label: "Adicionar texto");
        }
        else if (note)
        {
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddNote(input, page, box.X / DipsPerPoint, box.Y / DipsPerPoint, text, color, author, output), label: "Adicionar nota");
        }
        else
        {
            double fontSize = App.Settings.CommentFontSize;
            var style = new AnnotationStyle(color, LineWidth: 0);
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddTextBox(input, page, box.X / DipsPerPoint, box.Y / DipsPerPoint, box.Width / DipsPerPoint,
                    box.Height / DipsPerPoint, text, fontSize, style, author, output), label: "Adicionar caixa de texto");
        }
    }

    // ---------------- Carimbo ----------------

    private Border? _stampGhost;
    private DrawingSurface? _stampGhostSurface;

    private void Surface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _stampGhostSurface)) RemoveStampGhost();
        if (ReferenceEquals(sender, _imageGhostSurface) && _imageDragSurface is null) RemoveImageGhost();
    }

    /// <summary>Mostra (ou move) a prévia do carimbo centralizada no mouse.</summary>
    private void MoveStampGhost(DrawingSurface surface, Point p)
    {
        if (CurrentStamp() is not { } design) { RemoveStampGhost(); return; }
        if (!ReferenceEquals(surface, _stampGhostSurface)) RemoveStampGhost();
        var (w, h) = PdfAnnotations.StampSize(design);
        var color = Windows.UI.Color.FromArgb(255, design.Color.R, design.Color.G, design.Color.B);
        if (_stampGhost is null)
        {
            _stampGhost = new Border
            {
                BorderThickness = new Thickness(2.2 * DipsPerPoint),
                CornerRadius = new CornerRadius(6 * DipsPerPoint),
                Opacity = 0.55,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Arial"),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 18 * DipsPerPoint,
                },
            };
            _stampGhostSurface = surface;
            surface.Children.Add(_stampGhost);
        }
        _stampGhost.Width = w * DipsPerPoint;
        _stampGhost.Height = h * DipsPerPoint;
        _stampGhost.BorderBrush = new SolidColorBrush(color);
        var label = (TextBlock)_stampGhost.Child;
        label.Text = design.Text.Trim().ToUpper(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        label.Foreground = new SolidColorBrush(color);
        Canvas.SetLeft(_stampGhost, p.X - _stampGhost.Width / 2);
        Canvas.SetTop(_stampGhost, p.Y - _stampGhost.Height / 2);
    }

    private void RemoveStampGhost()
    {
        if (_stampGhost is not null) _stampGhostSurface?.Children.Remove(_stampGhost);
        _stampGhost = null;
        _stampGhostSurface = null;
    }

    private async Task PlaceStampAsync(PageSlot slot, Point p)
    {
        if (CurrentStamp() is not { } design)
        {
            ShowStatus(InfoBarSeverity.Informational, "Digite o texto do carimbo", "Escreva o texto no campo ao lado de \"Modelo\" e clique na página.");
            StampTextBox.Focus(FocusState.Programmatic);
            return;
        }
        RemoveStampGhost();
        int page = slot.Index;
        string author = App.Settings.CommentAuthor;
        await ApplyEditAsync((input, output) =>
            PdfAnnotations.AddStamp(input, page, p.X / DipsPerPoint, p.Y / DipsPerPoint, design, author, output), label: "Adicionar carimbo");
    }

    // ---------------- Realçar / sublinhar / riscar: selecionar o texto ----------------

    private Canvas? _selLayer;                     // retângulos da seleção, mostrados enquanto arrasta
    private Task<List<TextWord>>? _selWordsTask;
    private List<TextWord>? _selWords;
    private List<TextWord> _selected = [];

    // Palavras de cada página, lidas uma vez por versão do arquivo.
    private readonly Dictionary<int, Task<List<TextWord>>> _wordCache = [];
    private string? _wordCachePath;

    private Task<List<TextWord>> WordsOfPage(int page)
    {
        string path = DisplayPath!;
        if (_wordCachePath != path) { _wordCache.Clear(); _wordCachePath = path; }
        if (!_wordCache.TryGetValue(page, out var task))
            _wordCache[page] = task = Task.Run(() => PdfTextLayout.Words(path, page));
        return task;
    }

    private async Task StartTextSelectionAsync(DrawingSurface surface, PageSlot slot, PointerRoutedEventArgs e)
    {
        CancelDrawing();
        surface.CapturePointer(e.Pointer);
        _drawSurface = surface;
        _drawSlot = slot;
        _drawStart = Clamp(e.GetCurrentPoint(surface).Position);
        _selected = [];
        var layer = new Canvas { IsHitTestVisible = false };
        _selLayer = layer;
        surface.Children.Add(layer);

        var task = WordsOfPage(slot.Index);
        _selWordsTask = task;
        var words = await task;
        if (!ReferenceEquals(layer, _selLayer)) return; // já soltou ou cancelou
        _selWords = words;
        if (words.Count == 0)
        {
            CancelDrawing();
            ShowStatus(InfoBarSeverity.Informational, "Esta página não tem texto para selecionar",
                "Ela provavelmente é uma imagem (documento escaneado). Use o retângulo ou o desenho livre para marcar.");
        }
    }

    /// <summary>Atualiza as palavras selecionadas entre o início do arraste e <paramref name="current"/> e as mostra.</summary>
    private void ShowSelection(Point current)
    {
        if (_selLayer is not { } layer || _selWords is not { Count: > 0 } words) return;
        var a = new Point(_drawStart.X / DipsPerPoint, _drawStart.Y / DipsPerPoint);
        var b = new Point(current.X / DipsPerPoint, current.Y / DipsPerPoint);
        // Começou longe de qualquer texto e quase não arrastou: não seleciona nada.
        bool nearText = words.Min(w => w.DistanceTo(a.X, a.Y)) < 12 || words.Min(w => w.DistanceTo(b.X, b.Y)) < 12;
        _selected = nearText ? PdfTextLayout.Select(words, a.X, a.Y, b.X, b.Y) : [];

        layer.Children.Clear();
        var c = ToolColor;
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, c.R, c.G, c.B));
        foreach (var (x, y, w, h) in PdfTextLayout.LineRects(_selected))
        {
            var rect = new Shapes.Rectangle { Width = w * DipsPerPoint, Height = h * DipsPerPoint, Fill = fill };
            Canvas.SetLeft(rect, x * DipsPerPoint);
            Canvas.SetTop(rect, y * DipsPerPoint);
            layer.Children.Add(rect);
        }
    }

    private async Task FinishTextSelectionAsync(Point current)
    {
        if (_tool is not { } tool || _drawSlot is not { } slot || _selWordsTask is not { } task) { CancelDrawing(); return; }
        var layer = _selLayer;
        _selWords ??= await task;
        if (!ReferenceEquals(layer, _selLayer)) return;
        ShowSelection(current);
        var selected = _selected;
        CancelDrawing();
        if (selected.Count == 0) return;

        var lines = PdfTextLayout.LineRects(selected);
        string text = PdfTextLayout.TextOf(selected);
        int page = slot.Index;
        var style = new AnnotationStyle(ToolColor, LineWidth: 1);
        string author = App.Settings.CommentAuthor;
        await ApplyEditAsync((input, output) => PdfAnnotations.AddTextMarkup(input, page, tool.Kind, lines, style, author, output, text), label: "Marcar texto");
    }

    /// <summary>Grava como um único comentário todos os traços do desenho livre feitos até agora.</summary>
    private async Task CommitInkAsync()
    {
        if (_inkSlot is null || _inkStrokes.Count == 0) { ClearInk(); return; }
        int page = _inkSlot.Index;
        var strokes = _inkStrokes.Where(s => s.Count >= 2)
            .Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(p => (p.X / DipsPerPoint, p.Y / DipsPerPoint)).ToList())
            .ToList();
        var style = ToolStyle;
        string author = App.Settings.CommentAuthor;
        ClearInk();
        if (strokes.Count == 0) return;
        await ApplyEditAsync((input, output) => PdfAnnotations.AddInk(input, page, strokes, style, author, output), label: "Desenho livre");
    }

    private void ClearInk()
    {
        if (_inkSurface is not null) foreach (var p in _inkPreviews) _inkSurface.Children.Remove(p);
        _inkPreviews.Clear();
        _inkStrokes.Clear();
        _inkSurface = null;
        _inkSlot = null;
    }

    /// <summary>
    /// Página à qual um elemento do modelo pertence. O ItemsRepeater com x:Bind não preenche o DataContext,
    /// então sobe na árvore visual até o filho direto do repetidor e pergunta o índice dele.
    /// </summary>
    private PageSlot? SlotOf(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && VisualTreeHelper.GetParent(current) == PagesRepeater)
            {
                int index = PagesRepeater.GetElementIndex(ui);
                return index >= 0 && index < _slots.Count ? _slots[index] : null;
            }
        }
        return null;
    }

    /// <summary>Mantém o ponto dentro da página.</summary>
    private Point Clamp(Point p) => _drawSurface is null ? p
        : new(Math.Clamp(p.X, 0, _drawSurface.ActualWidth), Math.Clamp(p.Y, 0, _drawSurface.ActualHeight));

    /// <summary>Com Shift, prende a linha em múltiplos de 45° (horizontal, vertical, diagonal).</summary>
    private Point SnapAngle(Point end, bool snap)
    {
        if (!snap) return end;
        double dx = end.X - _drawStart.X, dy = end.Y - _drawStart.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return Clamp(new Point(_drawStart.X + length * Math.Cos(angle), _drawStart.Y + length * Math.Sin(angle)));
    }

    private static (double X, double Y, double W, double H) BoxBetween(Point a, Point b) =>
        (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private void PlaceBox(Point current)
    {
        var (x, y, w, h) = BoxBetween(_drawStart, current);
        Canvas.SetLeft(_preview, x);
        Canvas.SetTop(_preview, y);
        _preview!.Width = w;
        _preview.Height = h;
    }

    /// <summary>Remove o desenho provisório (exceto os traços do desenho livre) e solta o mouse.</summary>
    private void CancelDrawing()
    {
        if (_selLayer is not null)
        {
            _drawSurface?.Children.Remove(_selLayer);
            _selLayer = null;
            _selWords = null;
            _selWordsTask = null;
        }
        if (_drawSurface is not null)
        {
            if (_preview is Shapes.Polyline poly && _inkPreviews.Contains(poly)) { }
            else if (_preview is not null) _drawSurface.Children.Remove(_preview);
            _drawSurface.ReleasePointerCaptures();
        }
        _preview = null;
        _drawSurface = null;
        _drawSlot = null;
    }
}
