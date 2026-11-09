using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace GtViewer.Web.Services;

public interface IRescuePasswordService
{
    string GeneratePassword();
    string EncryptPassword(string plainPassword);
    string DecryptPassword(string encryptedPassword);
}

public class RescuePasswordService : IRescuePasswordService
{
    private readonly IDataProtector _protector;
    // Alfabeto legível de 20 caracteres sem caracteres confusos (como O/0 ou l/1)
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#%*+=-";

    public RescuePasswordService(IDataProtectionProvider protectionProvider)
    {
        _protector = protectionProvider.CreateProtector("GtViewer.RescuePasswords");
    }

    public string GeneratePassword()
    {
        var chars = new char[20];
        var bytes = new byte[20];
        RandomNumberGenerator.Fill(bytes);

        for (var i = 0; i < 20; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    public string EncryptPassword(string plainPassword)
    {
        return _protector.Protect(plainPassword);
    }

    public string DecryptPassword(string encryptedPassword)
    {
        return _protector.Unprotect(encryptedPassword);
    }
}
