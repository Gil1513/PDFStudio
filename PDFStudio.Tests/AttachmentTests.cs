using PDFStudio.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.4: anexos.</summary>
public class AttachmentTests
{
    [Fact]
    public void Add_List_Read_AndRemove()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string planilha = Path.Combine(dir, "orçamento.csv");
        File.WriteAllText(planilha, "item;valor\nserviço;12500\n");
        byte[] big = new byte[300_000];
        new Random(1).NextBytes(big);
        string foto = Path.Combine(dir, "foto.bin");
        File.WriteAllBytes(foto, big);

        Assert.Empty(PdfAttachments.List(input));
        long before = new FileInfo(input).Length;
        Assert.Equal("orçamento.csv", PdfAttachments.Add(input, planilha, "Valores do contrato", input));
        PdfAttachments.Add(input, foto, "", input);

        var list = PdfAttachments.List(input);
        Assert.Equal(["foto.bin", "orçamento.csv"], list.Select(a => a.Name)); // em ordem de nome
        var csv = list[1];
        Assert.Equal("Valores do contrato", csv.Description);
        Assert.Equal(new FileInfo(planilha).Length, csv.Size);
        Assert.Null(csv.PageIndex);
        Assert.NotNull(csv.Modified);
        Assert.Equal(File.ReadAllBytes(planilha), PdfAttachments.Read(input, csv));
        Assert.Equal(big, PdfAttachments.Read(input, list[0]));
        Assert.InRange(new FileInfo(input).Length - before, big.Length, big.Length + 5_000);

        // Arquivo repetitivo é guardado compactado.
        string texto = Path.Combine(dir, "repetido.txt");
        File.WriteAllText(texto, string.Concat(Enumerable.Repeat("cláusula padrão; ", 20_000)));
        long beforeText = new FileInfo(input).Length;
        PdfAttachments.Add(input, texto, "", input);
        Assert.True(new FileInfo(input).Length - beforeText < new FileInfo(texto).Length / 20);
        PdfAttachments.Remove(input, 2, input); // "repetido.txt" é o terceiro em ordem de nome

        string saved = Path.Combine(dir, "copia.csv");
        PdfAttachments.SaveTo(input, csv, saved);
        Assert.Equal(File.ReadAllText(planilha), File.ReadAllText(saved));

        PdfAttachments.Remove(input, 0, input);
        Assert.Equal(["orçamento.csv"], PdfAttachments.List(input).Select(a => a.Name));
        PdfAttachments.Remove(input, 0, input);
        Assert.Empty(PdfAttachments.List(input));
        Assert.Equal(2, TestPdfs.Labels(input).Count); // as páginas continuam lá
    }

    [Fact]
    public void Add_SameNameTwice_GetsANumber()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string file = Path.Combine(dir, "nota.txt");
        File.WriteAllText(file, "oi");

        PdfAttachments.Add(input, file, "", input);
        Assert.Equal("nota (2).txt", PdfAttachments.Add(input, file, "", input));
        Assert.Equal(["nota (2).txt", "nota.txt"], PdfAttachments.List(input).Select(a => a.Name));
    }

    [Fact]
    public void List_ReadsAttachmentsOfOtherPrograms_InTreesAndOnPages_WithSafeNames()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            PdfDictionary Spec(string fileName, string content)
            {
                var file = new PdfDictionary(doc);
                file.CreateStream(System.Text.Encoding.ASCII.GetBytes(content));
                doc.Internals.AddObject(file);
                var ef = new PdfDictionary(doc);
                ef.Elements["/F"] = file.Reference!;
                var spec = new PdfDictionary(doc);
                spec.Elements["/Type"] = new PdfName("/Filespec");
                spec.Elements["/F"] = new PdfString(fileName);
                spec.Elements["/EF"] = ef;
                return spec;
            }
            // Árvore com /Kids (como fazem outros programas) e um nome com pasta, que precisa ser descartada.
            var leaf = new PdfDictionary(doc);
            var names = new PdfArray(doc);
            names.Elements.Add(new PdfString("a"));
            names.Elements.Add(Spec(@"..\..\Windows\perigo.txt", "conteudo A"));
            leaf.Elements["/Names"] = names;
            doc.Internals.AddObject(leaf);
            var kids = new PdfArray(doc);
            kids.Elements.Add(leaf.Reference!);
            var root = new PdfDictionary(doc);
            root.Elements["/Kids"] = kids;
            var nameDict = new PdfDictionary(doc);
            nameDict.Elements["/EmbeddedFiles"] = root;
            doc.Internals.Catalog.Elements["/Names"] = nameDict;

            // Anexo preso à página (ícone de clipe).
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/FileAttachment");
            annot.Elements["/Rect"] = new PdfRectangle(new PdfSharp.Drawing.XPoint(10, 10), new PdfSharp.Drawing.XPoint(30, 30));
            annot.Elements["/FS"] = Spec("na:pagina.txt", "conteudo B");
            doc.Internals.AddObject(annot);
            var annots = new PdfArray(doc);
            annots.Elements.Add(annot.Reference!);
            doc.Pages[0].Elements["/Annots"] = annots;
            doc.Save(input + ".tmp");
        }
        File.Move(input + ".tmp", input, overwrite: true);

        var list = PdfAttachments.List(input);
        Assert.Equal(["perigo.txt", "na_pagina.txt"], list.Select(a => a.Name));
        Assert.Equal([null, 0], list.Select(a => a.PageIndex));
        Assert.Equal("conteudo B", System.Text.Encoding.ASCII.GetString(PdfAttachments.Read(input, list[1])));

        // Acrescentar um anexo novo mantém o que veio de outro programa.
        string file = Path.Combine(dir, "z.txt");
        File.WriteAllText(file, "z");
        PdfAttachments.Add(input, file, "", input);
        Assert.Equal(["perigo.txt", "z.txt", "na_pagina.txt"], PdfAttachments.List(input).Select(a => a.Name));
    }

    [Theory]
    [InlineData("contrato.pdf", false)]
    [InlineData("foto.JPG", false)]
    [InlineData("instalar.exe", true)]
    [InlineData("script.PS1", true)]
    [InlineData("planilha.xlsm", true)]
    public void IsRisky_FlagsProgramsAndScripts(string name, bool risky) => Assert.Equal(risky, PdfAttachments.IsRisky(name));
}
