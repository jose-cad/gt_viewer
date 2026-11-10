using GtViewer.Web.Data;
using GtViewer.Web.Models;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Login/Ativacao");
    options.Conventions.AllowAnonymousToPage("/Login/Verificar2Fa");
    options.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services.AddDbContext<GtViewerDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(connectionString);
});

builder.Services.AddDataProtection();

// Autenticação baseada em Cookie com expiração absoluta de 8 horas e reconferência periódica no AD
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/error?code=403";
        options.ExpireTimeSpan = TimeSpan.FromHours(8); // Limite de 8 horas conforme especificação J1
        options.SlidingExpiration = false; // Expiração estrita/absoluta
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.EventsType = typeof(AdSessionRecheckEvents);
    });

builder.Services.AddScoped<AdSessionRecheckEvents>();
builder.Services.AddScoped<IAuditoriaService, AuditoriaService>();
builder.Services.AddScoped<ILdapService, LdapService>();
builder.Services.AddSingleton<ITotpService, TotpService>();
builder.Services.AddSingleton<IActivationCodeService, ActivationCodeService>();
builder.Services.AddSingleton<ILoginSecurityService, LoginSecurityService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITecnicoService, TecnicoService>();
builder.Services.AddScoped<IServerCliService, ServerCliService>();
builder.Services.AddSingleton<IRescuePasswordService, RescuePasswordService>();
builder.Services.AddSingleton<IAgentScriptProvider, AgentScriptProvider>();
builder.Services.AddScoped<IFilaAcoesService, FilaAcoesService>();
builder.Services.AddScoped<IBloqueioService, BloqueioService>();
builder.Services.AddScoped<IReiniciarService, ReiniciarService>();
builder.Services.AddScoped<ISuporteRemotoService, SuporteRemotoService>();
builder.Services.AddScoped<IInventarioService, InventarioService>();
builder.Services.AddSingleton<IAuthenticodeValidator, AuthenticodeValidator>();
builder.Services.AddScoped<ICatalogoService, CatalogoService>();

builder.Services.AddAuthorization(options =>
{
    foreach (var permissao in Permissoes.Todas)
    {
        options.AddPolicy(permissao, policy =>
            policy.RequireAssertion(ctx => ctx.User.HasPermissao(permissao)));
    }
});

builder.Services.Configure<LdapOptions>(
    builder.Configuration.GetSection(LdapOptions.SectionName));

builder.Services.Configure<MeshCentralOptions>(
    builder.Configuration.GetSection(MeshCentralOptions.SectionName));
builder.Services.AddSingleton<MeshLoginTokenService>();
builder.Services.AddSingleton<MeshCentralClient>();
builder.Services.AddSingleton<IMeshCentralClient>(sp => sp.GetRequiredService<MeshCentralClient>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MeshCentralClient>());
builder.Services.AddHostedService<InventarioBackgroundService>();
builder.Services.AddHostedService<FilaAcoesBackgroundService>();
builder.Services.AddHostedService<SuporteRemotoWorker>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Execução de comandos administrativos CLI no servidor (Invariante 183 / Tarefa 6)
if (args.Length > 0 && (args[0] == "--create-admin" || args[0] == "--setup-emergency" || args[0] == "--enable-emergency"))
{
    using var scope = app.Services.CreateScope();
    var cliService = scope.ServiceProvider.GetRequiredService<IServerCliService>();

    try
    {
        if (args[0] == "--create-admin")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Uso: dotnet run -- --create-admin <usuario_dominio> <nome_exibicao> [email]");
                return;
            }
            var output = await cliService.CreateFirstAdminAsync(args[1], args[2], args.Length > 3 ? args[3] : null);
            Console.WriteLine(output);
            return;
        }
        else if (args[0] == "--setup-emergency")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Uso: dotnet run -- --setup-emergency <senha>");
                return;
            }
            var output = await cliService.SetupEmergencyAccountAsync(args[1]);
            Console.WriteLine(output);
            return;
        }
        else if (args[0] == "--enable-emergency")
        {
            var hours = 24;
            if (args.Length > 1 && int.TryParse(args[1], out var parsedHours))
            {
                hours = parsedHours;
            }
            var output = await cliService.EnableEmergencyAccountAsync(hours);
            Console.WriteLine(output);
            return;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERRO CLI]: {ex.Message}");
        Console.ResetColor();
        return;
    }
}

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// Endpoint seguro para entrega de senha de resgate ao agente via token de uso único (spec 2.5)
app.MapGet("/agente/senha-resgate", async (
    HttpContext context,
    IBloqueioService bloqueioService,
    ILoginSecurityService securityService) =>
{
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (securityService.IsIpBlocked(clientIp))
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    var authHeader = context.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        securityService.RecordFailedAttempt(null, clientIp);
        return Results.Unauthorized();
    }

    var token = authHeader["Bearer ".Length..].Trim();
    var senha = await bloqueioService.ObterSenhaAgentePorTokenAsync(token);
    if (senha == null)
    {
        securityService.RecordFailedAttempt(null, clientIp);
        return Results.Unauthorized();
    }

    return Results.Text(senha, "text/plain");
});

// Endpoints seguros para download de instaladores do catálogo via token de 30 min com suporte a retomada (Range)
app.MapGet("/agente/download/{token}", async (
    string token,
    HttpContext context,
    ICatalogoService catalogoService,
    ILoginSecurityService securityService) =>
{
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (securityService.IsIpBlocked(clientIp))
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    var result = await catalogoService.ObterInstaladorPorTokenAsync(token);
    if (result == null)
    {
        // Rate limit de /agente/* só conta tokens inválidos (spec 2.7 / Tarefa 14)
        securityService.RecordFailedAttempt(null, clientIp);
        return Results.Unauthorized();
    }

    return Results.Stream(
        result.Stream,
        contentType: result.ContentType,
        fileDownloadName: result.FileName,
        enableRangeProcessing: true);
});

app.MapGet("/agente/instalador", async (
    HttpContext context,
    ICatalogoService catalogoService,
    ILoginSecurityService securityService) =>
{
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (securityService.IsIpBlocked(clientIp))
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    var authHeader = context.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        securityService.RecordFailedAttempt(null, clientIp);
        return Results.Unauthorized();
    }

    var token = authHeader["Bearer ".Length..].Trim();
    var result = await catalogoService.ObterInstaladorPorTokenAsync(token);
    if (result == null)
    {
        securityService.RecordFailedAttempt(null, clientIp);
        return Results.Unauthorized();
    }

    return Results.Stream(
        result.Stream,
        contentType: result.ContentType,
        fileDownloadName: result.FileName,
        enableRangeProcessing: true);
});

app.Run();

namespace GtViewer.Web
{
    public partial class Program { }
}
