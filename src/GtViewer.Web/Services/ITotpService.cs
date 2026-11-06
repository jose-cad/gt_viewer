using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using QRCoder;

namespace GtViewer.Web.Services;

public interface ITotpService
{
    string GenerateSecret();
    string EncryptSecret(string plainSecret);
    string DecryptSecret(string encryptedSecret);
    string GenerateTotpUri(string username, string plainSecret);
    string GenerateQrCodeSvg(string uri);
    bool ValidateCode(int tecnicoId, string plainSecret, string code, out string? failureReason);
}

public class TotpService : ITotpService
{
    private readonly IDataProtector _protector;
    // Cache de códigos utilizados para impedir reuso (Anti-Replay)
    // Chave: "{tecnicoId}:{step}:{code}", Valor: timestamp de inserção
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UsedCodes = new();

    public TotpService(IDataProtectionProvider protectionProvider)
    {
        _protector = protectionProvider.CreateProtector("GtViewer.TotpSecrets");
    }

    public string GenerateSecret()
    {
        var buffer = new byte[20]; // 160 bits
        RandomNumberGenerator.Fill(buffer);
        return Base32Encode(buffer);
    }

    public string EncryptSecret(string plainSecret)
    {
        return _protector.Protect(plainSecret);
    }

    public string DecryptSecret(string encryptedSecret)
    {
        return _protector.Unprotect(encryptedSecret);
    }

    public string GenerateTotpUri(string username, string plainSecret)
    {
        var escapedUser = Uri.EscapeDataString(username);
        return $"otpauth://totp/gt_viewer:{escapedUser}?secret={plainSecret}&issuer=gt_viewer&digits=6&period=30";
    }

    public string GenerateQrCodeSvg(string uri)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new SvgQRCode(qrCodeData);
        return qrCode.GetGraphic(5);
    }

    public static string ComputeCurrentTotpCode(string plainSecret)
    {
        var key = Base32Decode(plainSecret);
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return ComputeTotp(key, step);
    }

    public bool ValidateCode(int tecnicoId, string plainSecret, string code, out string? failureReason)
    {
        failureReason = null;
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != 6 || !int.TryParse(code.Trim(), out _))
        {
            failureReason = "Código TOTP inválido (deve conter 6 dígitos).";
            return false;
        }

        code = code.Trim();
        var key = Base32Decode(plainSecret);
        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        // Limpeza periódica de códigos antigos (> 2 minutos)
        CleanupUsedCodes();

        // Janela de tolerância padrão: step - 1, step, step + 1
        for (var step = currentStep - 1; step <= currentStep + 1; step++)
        {
            var expectedCode = ComputeTotp(key, step);
            if (expectedCode == code)
            {
                var replayKey = $"{tecnicoId}:{step}:{code}";
                if (!UsedCodes.TryAdd(replayKey, DateTimeOffset.UtcNow))
                {
                    failureReason = "Código TOTP já utilizado (reuso não permitido).";
                    return false;
                }

                return true;
            }
        }

        failureReason = "Código TOTP incorreto.";
        return false;
    }

    private static void CleanupUsedCodes()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-3);
        foreach (var entry in UsedCodes)
        {
            if (entry.Value < cutoff)
            {
                UsedCodes.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string ComputeTotp(byte[] key, long step)
    {
        var stepBytes = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(stepBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(stepBytes);

        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var code = binary % 1_000_000;
        return code.ToString("D6");
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder();
        int buffer = 0, bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                output.Append(alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            buffer <<= (5 - bitsLeft);
            output.Append(alphabet[buffer & 0x1F]);
        }

        return output.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.Trim().ToUpperInvariant();
        var output = new List<byte>();
        int buffer = 0, bitsLeft = 0;

        foreach (var c in input)
        {
            var val = alphabet.IndexOf(c);
            if (val < 0) continue; // ignora padding ou caracteres estranhos

            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return output.ToArray();
    }
}
