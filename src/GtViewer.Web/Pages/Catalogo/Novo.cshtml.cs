using System.ComponentModel.DataAnnotations;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Catalogo;

[Authorize]
public class NovoModel : PageModel
{
    private readonly ICatalogoService _catalogoService;

    public NovoModel(ICatalogoService catalogoService)
    {
        _catalogoService = catalogoService;
    }

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
    [Required(ErrorMessage = "O instalador (.msi ou .exe) é obrigatório.")]
    public IFormFile? Arquivo { get; set; }

    public string? MensagemErro { get; set; }

    public IActionResult OnGet()
    {
        if (!User.HasPermissao(Permissoes.GerenciarCatalogo))
        {
            return Forbid();
        }

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

        if (Arquivo == null || Arquivo.Length == 0)
        {
            ModelState.AddModelError("Arquivo", "Selecione um instalador válido (.msi ou .exe).");
            return Page();
        }

        try
        {
            var ator = User.Identity?.Name ?? "desconhecido";
            var input = new CadastrarAppInput(
                Nome,
                Versao,
                FabricanteEsperado,
                ParametrosInstalacao,
                ComandoRemocao,
                RegraDeteccaoTipo,
                RegraDeteccaoChave,
                RegraDeteccaoValor
            );

            await using var stream = Arquivo.OpenReadStream();
            await _catalogoService.CadastrarAppAsync(input, stream, Arquivo.FileName, ator);

            TempData["MensagemSucesso"] = $"Aplicativo '{Nome}' cadastrado com sucesso no catálogo.";
            return RedirectToPage("/Catalogo/Index");
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
            return Page();
        }
    }
}
