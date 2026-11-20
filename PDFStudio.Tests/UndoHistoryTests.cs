using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.10: revisão do desfazer (nome de cada alteração e ferramentas de página no histórico).</summary>
public class UndoHistoryTests
{
    [Fact]
    public void Labels_FollowUndoAndRedo()
    {
        string dir = TestPdfs.TempDir();
        string original = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 4);
        using var work = new WorkingCopy(original, Path.Combine(dir, "work"));
        Assert.Null(work.UndoLabel);

        work.Apply((i, o) => PdfOperations.DeletePages(i, "1", o), "Excluir páginas");
        work.Apply((i, o) => PdfOperations.InsertBlankPages(i, 1, 2, o), "Inserir páginas");
        Assert.Equal("Inserir páginas", work.UndoLabel);
        Assert.Null(work.RedoLabel);

        work.Undo();
        Assert.Equal("Excluir páginas", work.UndoLabel);
        Assert.Equal("Inserir páginas", work.RedoLabel);

        work.Undo();
        Assert.Null(work.UndoLabel);
        Assert.Equal("Excluir páginas", work.RedoLabel);

        // Alteração nova apaga o que podia ser refeito, e o nome dela vai junto.
        work.Apply((i, o) => PdfOperations.DeletePages(i, "4", o), "Excluir a última");
        Assert.Equal("Excluir a última", work.UndoLabel);
        Assert.Null(work.RedoLabel);
    }

    [Fact]
    public void Alteration_WithoutLabel_StillUndoes()
    {
        string dir = TestPdfs.TempDir();
        string original = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        using var work = new WorkingCopy(original, Path.Combine(dir, "work"));
        work.Apply((i, o) => PdfOperations.DeletePages(i, "2", o));
        Assert.True(work.CanUndo);
        Assert.Null(work.UndoLabel);
    }

    [Fact]
    public void PageTools_InTheHistory_KeepTheOriginal_UntilSave()
    {
        // Antes desta etapa, "substituir o arquivo aberto" gravava direto no original. Agora vira uma versão:
        // excluir, inserir, recortar e redimensionar podem ser desfeitos e o original só muda ao salvar.
        string dir = TestPdfs.TempDir();
        string original = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        using var work = new WorkingCopy(original, Path.Combine(dir, "work"));

        work.Apply((i, o) => PdfOperations.DeletePages(i, "2", o), "Excluir páginas");
        work.Apply((i, o) => PdfOperations.CropPages(i, null, new PageMargins(20, 20, 20, 20), o), "Recortar páginas");
        work.Apply((i, o) => PdfOperations.ResizePages(i, null, 400, 400, PageOrientationChoice.Auto, true, o), "Redimensionar páginas");
        Assert.Equal(["Página 1", "Página 3"], TestPdfs.Labels(work.CurrentPath));
        Assert.Equal(["Página 1", "Página 2", "Página 3"], TestPdfs.Labels(original)); // o arquivo do usuário não mudou

        work.Undo(); work.Undo(); work.Undo();
        Assert.Equal(3, TestPdfs.Labels(work.CurrentPath).Count);

        work.Redo();
        work.Save();
        Assert.Equal(["Página 1", "Página 3"], TestPdfs.Labels(original));
    }
}
