using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Core;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Etapa 7.9: ler em voz alta. Usa as vozes que já vêm no Windows (no próprio computador, nada vai para a internet).
// Lê frase por frase a partir da página atual, destaca em azul a frase que está sendo lida e rola a tela junto.
// Barra com voz, velocidade, pausar/continuar e parar.
public sealed partial class ViewerPage
{
    private const string ReadMarkTag = "leitura";
    private static readonly SolidColorBrush ReadBrush = new(Windows.UI.Color.FromArgb(70, 0, 120, 215));
    private static readonly SolidColorBrush ReadStroke = new(Windows.UI.Color.FromArgb(255, 0, 103, 192));
    private static readonly double[] ReadSpeeds = [0.75, 1, 1.25, 1.5, 2];

    private SpeechSynthesizer? _synth;
    private MediaPlayer? _player;
    private CancellationTokenSource? _readCancel;
    private ReadingChunk? _readingNow;
    private bool _readPaused, _readSettingsChanged, _readControlsReady;

    private async Task ReadAloudAsync()
    {
        if (_readCancel is not null) { StopReadAloud(); return; } // o mesmo botão para a leitura
        if (DisplayPath is not { } source || _slots.Count == 0) return;
        if (SpeechSynthesizer.AllVoices.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "O Windows não tem vozes instaladas",
                "Em Configurações → Hora e idioma → Fala, adicione uma voz (por exemplo, Português (Brasil)) e tente de novo.");
            return;
        }

        var cancel = _readCancel = new CancellationTokenSource();
        List<ReadingChunk> chunks;
        try
        {
            int first = CurrentIndex;
            var pages = await Task.Run(() => PdfTextLayout.AllWords(source));
            chunks = ReadAloud.Chunks(pages, first);
            if (chunks.Count == 0 && first > 0) chunks = ReadAloud.Chunks(pages, 0); // daqui para a frente não há texto: lê do começo
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { chunks = []; }
        if (cancel.IsCancellationRequested || chunks.Count == 0)
        {
            // Parou enquanto preparava, ou não há texto: libera o botão para ler de novo.
            if (_readCancel == cancel) _readCancel = null;
            bool stopped = cancel.IsCancellationRequested;
            cancel.Dispose();
            if (stopped) return;
            ShowStatus(InfoBarSeverity.Informational, "Não há texto para ler",
                "Este PDF parece ser imagem escaneada. Faça o OCR (Ferramentas → OCR) e depois peça para ler de novo.");
            return;
        }

        bool finished = false;
        try
        {
            PrepareReadControls();
            _synth ??= new SpeechSynthesizer();
            _player ??= new MediaPlayer { AutoPlay = false };
            ApplyReadSettings();
            _readPaused = false;
            UpdatePauseButton();
            StatusBar.IsOpen = false;
            ReadAloudBar.Visibility = Visibility.Visible;

            var next = Synthesize(chunks[0].Text);
            for (int i = 0; i < chunks.Count; i++)
            {
                if (cancel.IsCancellationRequested || DisplayPath != source) break; // parou, ou o documento mudou
                ShowReading(chunks[i]);
                var stream = await next;
                if (_readSettingsChanged)
                {
                    // Trocou a voz ou a velocidade: a frase que já estava pronta é refeita com o novo ajuste.
                    _readSettingsChanged = false;
                    stream.Dispose();
                    stream = await Synthesize(chunks[i].Text);
                }
                next = i + 1 < chunks.Count ? Synthesize(chunks[i + 1].Text) : Task.FromResult<SpeechSynthesisStream>(null!);
                using (stream) await PlayAsync(stream, cancel.Token);
            }
            finished = !cancel.IsCancellationRequested && DisplayPath == source;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível ler em voz alta", ex.Message);
        }
        finally
        {
            if (_readCancel == cancel) _readCancel = null;
            cancel.Dispose();
            if (_player is not null) { _player.Pause(); _player.Source = null; }
            _readingNow = null;
            RefreshReadMarks();
            ReadAloudBar.Visibility = Visibility.Collapsed;
        }
        if (finished) ShowStatus(InfoBarSeverity.Informational, "Leitura terminada", "Chegou ao fim do documento.");
    }

    private async Task<SpeechSynthesisStream> Synthesize(string text) => await _synth!.SynthesizeTextToStreamAsync(text);

    /// <summary>Toca o áudio da frase e espera terminar (ou a leitura ser parada).</summary>
    private async Task PlayAsync(SpeechSynthesisStream stream, CancellationToken token)
    {
        var player = _player!;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<MediaPlayer, object> ended = (_, _) => done.TrySetResult();
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, e) => done.TrySetException(new InvalidOperationException(e.ErrorMessage));
        player.MediaEnded += ended;
        player.MediaFailed += failed;
        using var registration = token.Register(() => done.TrySetCanceled());
        try
        {
            player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            if (!_readPaused) player.Play();
            await done.Task;
        }
        finally
        {
            player.MediaEnded -= ended;
            player.MediaFailed -= failed;
            player.Source = null; // solta o áudio desta frase antes de ele ser fechado
        }
    }

    private void StopReadAloud()
    {
        _readCancel?.Cancel();
        if (_player is not null) { _player.Pause(); _player.Source = null; }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopReadAloud(); // voltou para a tela inicial: a leitura não continua escondida
        _stampWhenLeft = _path is not null && !HasUnsavedChanges ? FileStamp(_path) : null;
    }

    // ---------------- Barra: voz, velocidade, pausar, parar ----------------

    private void PrepareReadControls()
    {
        if (_readControlsReady) return;
        var voices = SpeechSynthesizer.AllVoices;
        foreach (var v in voices) ReadAloudVoice.Items.Add($"{v.DisplayName.Replace("Microsoft ", "")} ({v.Language})");
        // Voz em português do Brasil, se houver; senão a voz padrão do Windows.
        int preferred = voices.ToList().FindIndex(v => v.Language.Equals("pt-BR", StringComparison.OrdinalIgnoreCase));
        if (preferred < 0) preferred = voices.ToList().FindIndex(v => v.Id == SpeechSynthesizer.DefaultVoice?.Id);
        ReadAloudVoice.SelectedIndex = Math.Max(0, preferred);
        foreach (var s in ReadSpeeds) ReadAloudSpeed.Items.Add(s.ToString("0.##", new System.Globalization.CultureInfo("pt-BR")) + "×");
        ReadAloudSpeed.SelectedIndex = Array.IndexOf(ReadSpeeds, 1.0);
        _readControlsReady = true;
    }

    private void ApplyReadSettings()
    {
        if (_synth is null) return;
        var voices = SpeechSynthesizer.AllVoices;
        if (ReadAloudVoice.SelectedIndex >= 0 && ReadAloudVoice.SelectedIndex < voices.Count)
            _synth.Voice = voices[ReadAloudVoice.SelectedIndex];
        if (ReadAloudSpeed.SelectedIndex >= 0) _synth.Options.SpeakingRate = ReadSpeeds[ReadAloudSpeed.SelectedIndex];
    }

    private void ReadAloudVoice_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadSettingsChanged();
    private void ReadAloudSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadSettingsChanged();

    private void ReadSettingsChanged()
    {
        if (!_readControlsReady) return;
        ApplyReadSettings();
        _readSettingsChanged = _readCancel is not null; // vale a partir da próxima frase
    }

    private void ReadAloudPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || _readCancel is null) return;
        _readPaused = !_readPaused;
        if (_readPaused) _player.Pause();
        else _player.Play();
        UpdatePauseButton();
    }

    private void ReadAloudStop_Click(object sender, RoutedEventArgs e) => StopReadAloud();

    private void UpdatePauseButton()
    {
        string label = _readPaused ? "Continuar" : "Pausar";
        ReadAloudPauseIcon.Glyph = _readPaused ? "" : "";
        ToolTipService.SetToolTip(ReadAloudPauseButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReadAloudPauseButton, label);
    }

    // ---------------- Destaque e rolagem ----------------

    /// <summary>Mostra a frase lida: texto na barra, destaque azul e, se ela estiver fora da tela, rola até ela.</summary>
    private void ShowReading(ReadingChunk chunk)
    {
        _readingNow = chunk;
        ReadAloudText.Text = $"Página {chunk.Page + 1}: {chunk.Text}";
        RefreshReadMarks();
        if (chunk.Boxes.Count == 0 || chunk.Page >= _slots.Count) return;
        double zoom = Scroller.ZoomFactor;
        double top = (PageTop(chunk.Page) + chunk.Boxes.Min(b => b.Y) * DipsPerPoint) * zoom;
        double bottom = (PageTop(chunk.Page) + chunk.Boxes.Max(b => b.Y + b.Height) * DipsPerPoint) * zoom;
        if (top < Scroller.VerticalOffset || bottom > Scroller.VerticalOffset + Scroller.ViewportHeight)
        {
            Scroller.ChangeView(null, Math.Max(0, top - Scroller.ViewportHeight / 3), null, false);
            SetPageBox(chunk.Page + 1);
        }
    }

    private void RefreshReadMarks()
    {
        for (int i = 0; i < _slots.Count; i++)
            if (PagesRepeater.TryGetElement(i) is { } element) ShowReadMarks(element, i);
    }

    /// <summary>Põe na página o destaque da frase que está sendo lida (se for desta página) e tira o anterior.</summary>
    private void ShowReadMarks(UIElement pageElement, int pageIndex)
    {
        if (SurfaceIn(pageElement) is not { } surface) return;
        for (int i = surface.Children.Count - 1; i >= 0; i--)
            if (surface.Children[i] is FrameworkElement { Tag: ReadMarkTag }) surface.Children.RemoveAt(i);
        if (_readingNow is not { } chunk || chunk.Page != pageIndex) return;
        int insertAt = 0; // por baixo dos comentários
        foreach (var box in chunk.Boxes)
        {
            var shape = new Shapes.Rectangle
            {
                Width = Math.Max(1, box.Width * DipsPerPoint + 4),
                Height = Math.Max(1, box.Height * DipsPerPoint + 2),
                Fill = ReadBrush,
                Stroke = ReadStroke,
                StrokeThickness = 1,
                RadiusX = 3, RadiusY = 3,
                IsHitTestVisible = false,
                Tag = ReadMarkTag,
            };
            Canvas.SetLeft(shape, box.X * DipsPerPoint - 2);
            Canvas.SetTop(shape, box.Y * DipsPerPoint - 1);
            surface.Children.Insert(insertAt++, shape);
        }
    }
}
