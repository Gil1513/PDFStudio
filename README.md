# PDF Studio

Aplicativo para Windows que lê, edita, converte, assina e protege PDFs.
**Funciona só no seu computador:** os arquivos nunca são enviados para um servidor. A única exceção é o
carimbo de tempo opcional na assinatura, que só usa a internet se você pedir.

Feito em **WinUI 3 (Windows App SDK) + C# / .NET 10**.

## Funções

| Área | O que faz |
|---|---|
| **Ver** | Abrir PDFs com zoom, miniaturas, marcadores, busca, leitura em voz alta e tela cheia |
| **Editar** | Editar textos e imagens do próprio PDF, adicionar texto, imagem e link, marca d'água, número de página, cabeçalho e rodapé, fundo |
| **Comentar** | Realçar, sublinhar, riscar, linha, seta, retângulo, oval, desenho livre, caixa de texto, nota e carimbo; mover e redimensionar comentários |
| **Páginas** | Combinar, dividir, organizar (girar e reordenar), inserir, extrair, excluir, recortar e redimensionar páginas |
| **Converter** | PDF para Word e para imagem (PNG/JPG), imagens para PDF, OCR (texto pesquisável em PDFs digitalizados), comprimir |
| **Formulários** | Preencher campos, exportar os dados para CSV, achatar campos e comentários |
| **Proteger** | Colocar e remover senha (AES), respeitar as permissões do autor (imprimir, copiar), redigir de verdade (apaga o conteúdo por baixo da tarja) |
| **Assinar** | Assinatura digital com certificado `.pfx`, visível ou invisível, com carimbo de tempo opcional; campos de assinatura; conferência das assinaturas (válida, válida com alterações permitidas, inválida); comentários e novas assinaturas sem invalidar as anteriores |
| **Lote** | A mesma ação em vários PDFs de uma vez (marca d'água, senha, compressão, conversão, numeração Bates, páginas em branco e outras) |
| **Imprimir** | Impressão com prévia e ajuste de escala |

## Requisitos

- Windows 10 (versão 1809) ou mais novo, ou Windows 11, 64 bits
- [SDK do .NET 10](https://dotnet.microsoft.com/download)
- Para editar a interface: Visual Studio 2026 com a carga "Desenvolvimento para Windows" (WinUI)

## Como compilar e rodar

```powershell
git clone https://github.com/Gil1513/PDFStudio.git
cd PDFStudio
dotnet build PDFStudio/PDFStudio.csproj -p:Platform=x64
```

O programa fica em `PDFStudio\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\PDFStudio.exe`.

## Testes

```powershell
dotnet test PDFStudio.Tests
```

São mais de 400 testes automáticos (xUnit) para a parte que mexe nos arquivos (`PDFStudio.Core`).

## Organização do código

| Pasta | Conteúdo |
|---|---|
| `PDFStudio` | Interface (telas, janelas de diálogo, desenho das páginas, impressão) |
| `PDFStudio.Core` | Tudo o que lê e grava PDFs, sem interface (dá para reaproveitar e testar) |
| `PDFStudio.Tests` | Testes automáticos |

## Bibliotecas usadas

| Biblioteca | Licença | Para quê |
|---|---|---|
| [PDFsharp](https://github.com/empira/PDFsharp) 6.2.4 | MIT | Ler, gravar e editar PDFs |
| [PdfPig](https://github.com/UglyToad/PdfPig) | Apache 2.0 | Ler o texto e as posições das letras |
| Windows App SDK / WinUI 3 | MIT | Interface |
| System.Security.Cryptography.Pkcs | MIT | Assinatura digital |
| Fontes Liberation, Carlito e Caladea | SIL OFL 1.1 | Fontes que acompanham o programa |

O desenho das páginas, o OCR e a leitura em voz alta usam recursos que já vêm no Windows.

## Licença

© 2026 Gilmar da Silva. **Todos os direitos reservados.** O código está aqui só para consulta.
Copiar, modificar, distribuir ou usar o código precisa de autorização por escrito. Veja [LICENSE.md](LICENSE.md).
