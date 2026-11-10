using System.ComponentModel.DataAnnotations;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Catalogo;

[Authorize]
public class EditarModel : PageModel
{
    private readonly ICatalogoService _catalogoService;

    public EditarModel(ICatalogoService catalogoService)
    {
        _catalogoService = catalogoService;
    }

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "O nome é obrigatório.")]
    [MaxLength(150)]
    public string Nome { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "A versão é obrigatória.")]
    [MaxLength(50)]
    public string Versao { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "O fabricante esperado no certificado Authenticode é obrigatório.")]
    [MaxLength(200)]
    public string FabricanteEsperado { get; set; } = string.Empty;

    [BindProperty]
    [MaxLength(500)]
    public string? ParametrosInstalacao { get; set; }

    [BindProperty]
    [MaxLength(500)]
    public string? ComandoRemocao { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "O tipo de regra de detecção é obrigatório.")]
    public string RegraDeteccaoTipo { get; set; } = "registro";

    [BindProperty]
    [Required(ErrorMessage = "A chave ou caminho de detecção é obrigatória.")]
    [MaxLength(500)]
    public string RegraDeteccaoChave { get; set; } = string.Empty;

    [BindProperty]
    [MaxLength(500)]
    public string? RegraDeteccaoValor { get; set; }

    [BindProperty]
    public bool Ativo { get; set; }

    [BindProperty]
    public IFormFile? NovoArquivo { get; set; }

    public string? HashSha256Atual { get; set; }
    public long TamanhoBytesAtual { get; set; }
    public string? MensagemErro { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!User.HasPermissao(Permissoes.GerenciarCatalogo))
        {
            return Forbid();
        }

        var app = await _catalogoService.ObterAppPorIdAsync(id);
        if (app == null)
        {
            return NotFound();
        }

        Id = app.Id;
        Nome = app.Nome;
        Versao = app.Versao;
        FabricanteEsperado = app.FabricanteEsperado;
        ParametrosInstalacao = app.ParametrosInstalacao;
        ComandoRemocao = app.ComandoRemocao;
        RegraDeteccaoTipo = app.RegraDeteccaoTipo;
        RegraDeteccaoChave = app.RegraDeteccaoChave;
        RegraDeteccaoValor = app.RegraDeteccaoValor;
        Ativo = app.Ativo;
        HashSha256Atual = app.HashSha256;
        TamanhoBytesAtual = app.TamanhoBytes;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!User.HasPermissao(Permissoes.GerenciarCatalogo))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var ator = User.Identity?.Name ?? "desconhecido";
            var input = new AtualizarAppInput(
                Nome,
                Versao,
                FabricanteEsperado,
                ParametrosInstalacao,
                ComandoRemocao,
                RegraDeteccaoTipo,
                RegraDeteccaoChave,
                RegraDeteccaoValor,
                Ativo
            );

            Stream? stream = null;
            string? fileName = null;
            if (NovoArquivo != null && NovoArquivo.Length > 0)
            {
                stream = NovoArquivo.OpenReadStream();
                fileName = NovoArquivo.FileName;
            }

            try
            {
                await _catalogoService.AtualizarAppAsync(Id, input, ator, stream, fileName);
            }
            finally
            {
                if (stream != null) await stream.DisposeAsync();
            }

            TempData["MensagemSucesso"] = $"Aplicativo '{Nome}' atualizado com sucesso.";
            return RedirectToPage("/Catalogo/Index");
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
            return Page();
        }
    }
}
