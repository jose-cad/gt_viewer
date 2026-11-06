using System.Security.Cryptography;
using System.Text;

namespace GtViewer.Web.Services;

public interface IActivationCodeService
{
    string GenerateActivationCode();
    string HashActivationCode(string code);
    bool VerifyActivationCode(string code, string expectedHash);
}

public class ActivationCodeService : IActivationCodeService
{
    private const string CharacterSet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // Sem 0, 1, O, I para evitar erros de digitação

    public string GenerateActivationCode()
    {
        var chars = new char[16];
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);

        for (int i = 0; i < 16; i++)
        {
            chars[i] = CharacterSet[bytes[i] % CharacterSet.Length];
        }

        // Formato amigável: XXXX-XXXX-XXXX-XXXX
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}-{new string(chars, 12, 4)}";
    }

    public string NormalizeCode(string code)
    {
        return code.Replace("-", "").Trim().ToUpperInvariant();
    }

    public string HashActivationCode(string code)
    {
        var normalized = NormalizeCode(code);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool VerifyActivationCode(string code, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var hash = HashActivationCode(code);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(expectedHash.ToLowerInvariant()));
    }
}
