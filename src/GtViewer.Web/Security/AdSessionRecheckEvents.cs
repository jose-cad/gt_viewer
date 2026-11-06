using System.Security.Claims;
using GtViewer.Web.Data;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Security;

public class AdSessionRecheckEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal == null)
        {
            context.RejectPrincipal();
            return;
        }

        var username = principal.Identity?.Name;
        var isEmergency = principal.FindFirst("IsContaEmergencia")?.Value == "True";
        var lastRecheckStr = principal.FindFirst("LastAdRecheckUtc")?.Value;

        if (string.IsNullOrWhiteSpace(username) || !long.TryParse(lastRecheckStr, out var lastRecheckSec))
        {
            context.RejectPrincipal();
            return;
        }

        var lastRecheck = DateTimeOffset.FromUnixTimeSeconds(lastRecheckSec);
        var now = DateTimeOffset.UtcNow;

        // Reconferência a cada 15 minutos (J1 passo 5 e Tarefa 6)
        if (now - lastRecheck < TimeSpan.FromMinutes(15))
        {
            return;
        }

        var dbContext = context.HttpContext.RequestServices.GetRequiredService<GtViewerDbContext>();
        var ldapService = context.HttpContext.RequestServices.GetRequiredService<ILdapService>();
        var auditoriaService = context.HttpContext.RequestServices.GetRequiredService<IAuditoriaService>();

        var tecnico = await dbContext.Tecnicos.FirstOrDefaultAsync(t => t.UsuarioDominio.ToLower() == username.ToLower());
        if (tecnico == null || !tecnico.Ativo)
        {
            // Técnico desativado no banco
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
            await auditoriaService.RegistrarAsync(username, TiposAuditoria.EncerrarSessaoDesativacaoAd, "gt_viewer", false, "Técnico inativo no sistema");
            return;
        }

        // Se for conta de emergência (Invariante 6 e Tarefa 6):
        if (isEmergency || tecnico.IsContaEmergencia)
        {
            // Verificar se as 24h expiraram
            if (tecnico.EmergenciaExpiraEm.HasValue && now > tecnico.EmergenciaExpiraEm.Value)
            {
                tecnico.Ativo = false;
                await dbContext.SaveChangesAsync();
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
                await auditoriaService.RegistrarAsync("sistema", TiposAuditoria.HabilitarExpirarContaEmergencia, "gt_viewer", false, "Sessão de emergência encerrada: 24h expiradas");
                return;
            }

            // Conta de emergência não passa pela reconferência no AD
            UpdateLastRecheckClaim(context, now);
            return;
        }

        // Usuário regular: reconferência no AD via LDAPS
        var adStatus = await ldapService.CheckUserStatusAsync(username);
        if (adStatus.IsConnectionError)
        {
            // Invariante e Tarefa 6: AD inacessível na reconferência -> a sessão continua (até o limite de 8 h) e o fato é registrado!
            await auditoriaService.RegistrarAsync("sistema", TiposAuditoria.FalhaReconferenciaAd, username, false, "AD inacessível durante reconferência periódica; sessão mantida");
            UpdateLastRecheckClaim(context, now);
            return;
        }

        if (!adStatus.IsActive || !adStatus.Exists)
        {
            // Conta desativada no AD -> encerra a sessão imediatamente
            tecnico.Ativo = false;
            await dbContext.SaveChangesAsync();

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
            await auditoriaService.RegistrarAsync(username, TiposAuditoria.EncerrarSessaoDesativacaoAd, "gt_viewer", false, "Conta desativada ou removida no AD");
            return;
        }

        // Usuário segue ativo
        UpdateLastRecheckClaim(context, now);
    }

    private static void UpdateLastRecheckClaim(CookieValidatePrincipalContext context, DateTimeOffset now)
    {
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            var oldClaim = identity.FindFirst("LastAdRecheckUtc");
            if (oldClaim != null)
            {
                identity.RemoveClaim(oldClaim);
            }
            identity.AddClaim(new Claim("LastAdRecheckUtc", now.ToUnixTimeSeconds().ToString()));
            context.ShouldRenew = true;
        }
    }
}
