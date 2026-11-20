namespace PDFStudio.Services;

/// <summary>
/// "Fila de tarefas" da janela com uma rede de segurança. No WinUI 3, um erro dentro de um botão "async void"
/// derruba o app sem passar por Application.UnhandledException. Tudo o que roda na janela (cliques e a
/// continuação de cada await) passa por <see cref="Post"/>; aqui o erro é pego, anotado e avisado, e o app
/// continua aberto com o trabalho não salvo.
/// </summary>
public sealed class SafeSynchronizationContext(SynchronizationContext inner, Action<Exception> onError) : SynchronizationContext
{
    /// <summary>Troca a fila da thread atual (a da janela) por esta, se ainda não foi trocada.</summary>
    public static void Install(Action<Exception> onError)
    {
        var current = Current;
        if (current is null or SafeSynchronizationContext) return;
        SetSynchronizationContext(new SafeSynchronizationContext(current, onError));
    }

    public override void Post(SendOrPostCallback d, object? state) => inner.Post(s => Run(d, s), state);

    public override void Send(SendOrPostCallback d, object? state) => inner.Send(s => Run(d, s), state);

    public override SynchronizationContext CreateCopy() => new SafeSynchronizationContext(inner.CreateCopy(), onError);

    public override void OperationStarted() => inner.OperationStarted();

    public override void OperationCompleted() => inner.OperationCompleted();

    private void Run(SendOrPostCallback d, object? state)
    {
        try
        {
            d(state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            onError(ex);
        }
    }
}
