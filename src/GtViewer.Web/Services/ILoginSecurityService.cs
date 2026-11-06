using System.Collections.Concurrent;
using System.Diagnostics;

namespace GtViewer.Web.Services;

public interface ILoginSecurityService
{
    bool IsIpBlocked(string ip);
    void RecordFailedAttempt(string? user, string ip);
    void RecordSuccessfulLogin(string user, string ip);
    Task ApplyProgressiveDelayAsync(string? user, string ip, CancellationToken cancellationToken = default);
    Task EnsureMinimumResponseTimeAsync(Stopwatch stopwatch, int minimumMilliseconds = 500, CancellationToken cancellationToken = default);
}

public class LoginSecurityService : ILoginSecurityService
{
    // Rastreamento por IP: timestamps das tentativas falhas no último minuto
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> IpFailedAttempts = new();
    // Bloqueio por IP: IP -> ExpiraEm (15 min)
    private static readonly ConcurrentDictionary<string, DateTimeOffset> IpBlockedUntil = new();
    // Tentativas falhas consecutivas por par (Usuário, IP) para atraso progressivo
    private static readonly ConcurrentDictionary<string, int> UserIpFailures = new();

    public bool IsIpBlocked(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;

        if (IpBlockedUntil.TryGetValue(ip, out var blockedUntil))
        {
            if (DateTimeOffset.UtcNow < blockedUntil)
            {
                return true;
            }
            IpBlockedUntil.TryRemove(ip, out _);
        }

        return false;
    }

    public void RecordFailedAttempt(string? user, string ip)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Atualizar contagem por IP (janela de 1 minuto)
        var queue = IpFailedAttempts.GetOrAdd(ip, _ => new ConcurrentQueue<DateTimeOffset>());
        queue.Enqueue(now);

        // Remover tentativas mais antigas que 1 minuto
        var oneMinuteAgo = now.AddMinutes(-1);
        while (queue.TryPeek(out var timestamp) && timestamp < oneMinuteAgo)
        {
            queue.TryDequeue(out _);
        }

        // Se mais de 10 tentativas falhas no último minuto, bloqueia o IP por 15 minutos
        if (queue.Count > 10)
        {
            IpBlockedUntil[ip] = now.AddMinutes(15);
        }

        // 2. Atualizar atraso progressivo por (Usuário, IP)
        if (!string.IsNullOrWhiteSpace(user))
        {
            var key = $"{user.Trim().ToLowerInvariant()}@{ip}";
            UserIpFailures.AddOrUpdate(key, 1, (_, current) => Math.Min(current + 1, 10));
        }
    }

    public void RecordSuccessfulLogin(string user, string ip)
    {
        if (!string.IsNullOrWhiteSpace(user))
        {
            var key = $"{user.Trim().ToLowerInvariant()}@{ip}";
            UserIpFailures.TryRemove(key, out _);
        }
    }

    public async Task ApplyProgressiveDelayAsync(string? user, string ip, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user)) return;

        var key = $"{user.Trim().ToLowerInvariant()}@{ip}";
        if (UserIpFailures.TryGetValue(key, out var failures) && failures > 0)
        {
            // Atraso progressivo: 100ms * número de falhas (máximo 1500ms)
            var delayMs = Math.Min(failures * 150, 1500);
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    public async Task EnsureMinimumResponseTimeAsync(Stopwatch stopwatch, int minimumMilliseconds = 500, CancellationToken cancellationToken = default)
    {
        var elapsed = stopwatch.ElapsedMilliseconds;
        var remaining = minimumMilliseconds - elapsed;
        if (remaining > 0)
        {
            await Task.Delay((int)remaining, cancellationToken);
        }
    }
}
