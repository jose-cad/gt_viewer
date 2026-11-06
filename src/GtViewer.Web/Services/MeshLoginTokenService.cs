using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GtViewer.Web.Models;
using Microsoft.Extensions.Options;

namespace GtViewer.Web.Services;

public class MeshLoginTokenService
{
    private readonly MeshCentralOptions _options;
    private readonly byte[] _aesKey;

    public MeshLoginTokenService(IOptions<MeshCentralOptions> options)
    {
        _options = options.Value;
        _aesKey = ParseAesKey(_options.LoginCookieEncryptionKey);
    }

    public MeshLoginTokenService(string hexKey, string serverUrl = "https://mesh.localhost")
    {
        _options = new MeshCentralOptions
        {
            LoginCookieEncryptionKey = hexKey,
            ServerUrl = serverUrl
        };
        _aesKey = ParseAesKey(hexKey);
    }

    private static byte[] ParseAesKey(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw new ArgumentException("LoginCookieEncryptionKey cannot be empty.", nameof(hex));
        }

        var keyBytes = Convert.FromHexString(hex.Trim());
        if (keyBytes.Length < 32)
        {
            throw new ArgumentException($"LoginCookieEncryptionKey must be at least 32 bytes (64 hex characters), but was {keyBytes.Length} bytes.", nameof(hex));
        }

        // MeshCentral uses key.slice(0, 32) for AES-256-GCM
        return keyBytes[..32];
    }

    /// <summary>
    /// Gera um token cifrado em AES-256-GCM compatível com o MeshCentral (decodeCookieAESGCM).
    /// </summary>
    public string GenerateLoginToken(string username, int? expireMinutes = null)
    {
        var expire = expireMinutes ?? _options.DefaultExpireMinutes;
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var payload = new MeshLoginTokenPayload
        {
            User = username.StartsWith("user/") ? username : $"user//{username}",
            AuthType = 3,
            ExpireMinutes = expire,
            Time = nowSeconds
        };

        var json = JsonSerializer.Serialize(payload);
        var plaintext = Encoding.UTF8.GetBytes(json);

        var iv = new byte[12];
        RandomNumberGenerator.Fill(iv);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(_aesKey, 16))
        {
            aes.Encrypt(iv, plaintext, ciphertext, tag);
        }

        // MeshCentral format: [12 bytes IV] + [16 bytes Tag] + [Ciphertext]
        var combined = new byte[12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, 12);
        Buffer.BlockCopy(tag, 0, combined, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, combined, 28, ciphertext.Length);

        // Base64 com substituição padrão do MeshCentral: '+' -> '@', '/' -> '$'
        return Convert.ToBase64String(combined)
            .Replace('+', '@')
            .Replace('/', '$');
    }

    /// <summary>
    /// Decifra um token de login gerado no padrão do MeshCentral.
    /// Retorna null se for inválido ou adulterado.
    /// </summary>
    public MeshLoginTokenPayload? DecryptLoginToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var base64 = token.Replace('@', '+').Replace('$', '/');
            var raw = Convert.FromBase64String(base64);

            if (raw.Length < 29) // 12 (IV) + 16 (Tag) + >= 1 byte ciphertext
            {
                return null;
            }

            var iv = raw[..12];
            var tag = raw[12..28];
            var ciphertext = raw[28..];
            var plaintext = new byte[ciphertext.Length];

            using (var aes = new AesGcm(_aesKey, 16))
            {
                aes.Decrypt(iv, ciphertext, tag, plaintext);
            }

            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<MeshLoginTokenPayload>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verifica se o payload decifrado ainda está dentro do período de validade (2 minutos por padrão).
    /// </summary>
    public bool IsTokenValid(MeshLoginTokenPayload? payload, DateTimeOffset? now = null)
    {
        if (payload == null || payload.Time <= 0)
        {
            return false;
        }

        var currentSeconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var deltaSeconds = currentSeconds - payload.Time;

        // Tolerância de 30s no passado (relógios ligeiramente dessincronizados)
        if (deltaSeconds < -30)
        {
            return false;
        }

        var maxLifetimeSeconds = payload.ExpireMinutes * 60;
        return deltaSeconds <= maxLifetimeSeconds;
    }

    /// <summary>
    /// Constrói a URL do iframe de tela remota com o viewmode validado (viewmode=11: tela direta).
    /// </summary>
    public string BuildRemoteDesktopUrl(string token, string nodeId, int viewmode = 11, int hide = 31)
    {
        var baseUrl = _options.ServerUrl.TrimEnd('/');
        return $"{baseUrl}/?login={Uri.EscapeDataString(token)}&gotonode={Uri.EscapeDataString(nodeId)}&viewmode={viewmode}&hide={hide}";
    }
}
