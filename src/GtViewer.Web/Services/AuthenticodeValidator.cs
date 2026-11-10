using System.Reflection.PortableExecutable;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace GtViewer.Web.Services;

public class AuthenticodeValidator : IAuthenticodeValidator
{
    private static readonly byte[] Pkcs7SignedDataOid = [0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02];

    public AuthenticodeValidationResult ValidateFile(string filePath, string? expectedPublisher = null)
    {
        if (!File.Exists(filePath))
        {
            return new AuthenticodeValidationResult(false, null, null, "Arquivo não encontrado no servidor.");
        }

        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".exe" || ext == ".dll")
            {
                return ValidatePeFile(filePath, expectedPublisher);
            }

            if (ext == ".msi")
            {
                return ValidateMsiFile(filePath, expectedPublisher);
            }

            return new AuthenticodeValidationResult(false, null, null, $"Formato de instalador não suportado: '{ext}'. Suportados: .msi, .exe.");
        }
        catch (Exception ex)
        {
            return new AuthenticodeValidationResult(false, null, null, $"Falha na validação de assinatura Authenticode: {ex.Message}");
        }
    }

    private static AuthenticodeValidationResult ValidatePeFile(string filePath, string? expectedPublisher)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 64)
        {
            return new AuthenticodeValidationResult(false, null, null, "Arquivo executável inválido ou truncado.");
        }

        using var peReader = new PEReader(stream);
        var headers = peReader.PEHeaders;
        if (headers.PEHeader == null)
        {
            return new AuthenticodeValidationResult(false, null, null, "Cabeçalho PE ausente no arquivo.");
        }

        var certDir = headers.PEHeader.CertificateTableDirectory;
        if (certDir.RelativeVirtualAddress == 0 || certDir.Size < 8)
        {
            return new AuthenticodeValidationResult(false, null, null, "Instaladores sem assinatura digital ficam fora do catálogo na v1.");
        }

        if (certDir.RelativeVirtualAddress + certDir.Size > stream.Length)
        {
            return new AuthenticodeValidationResult(false, null, null, "Tabela de certificados Authenticode inválida ou truncada.");
        }

        stream.Seek(certDir.RelativeVirtualAddress, SeekOrigin.Begin);
        using var reader = new BinaryReader(stream);
        var dwLength = reader.ReadInt32();
        var wRevision = reader.ReadInt16();
        var wCertificateType = reader.ReadInt16();

        if (dwLength <= 8 || dwLength > certDir.Size)
        {
            return new AuthenticodeValidationResult(false, null, null, "Tamanho de WIN_CERTIFICATE inválido.");
        }

        // 0x0002 = WIN_CERT_TYPE_PKCS_SIGNED_DATA
        if (wCertificateType != 0x0002)
        {
            return new AuthenticodeValidationResult(false, null, null, $"Tipo de certificado Authenticode não suportado (0x{wCertificateType:X4}).");
        }

        var certBytes = reader.ReadBytes(dwLength - 8);
        return ExtractSignerFromPkcs7(certBytes, expectedPublisher);
    }

    private static AuthenticodeValidationResult ValidateMsiFile(string filePath, string? expectedPublisher)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length < 512)
        {
            return new AuthenticodeValidationResult(false, null, null, "Arquivo MSI inválido ou truncado.");
        }

        // Verifica se é Compound File Binary Format (OLE): D0 CF 11 E0 A1 B1 1A E1
        if (bytes[0] != 0xD0 || bytes[1] != 0xCF || bytes[2] != 0x11 || bytes[3] != 0xE0 ||
            bytes[4] != 0xA1 || bytes[5] != 0xB1 || bytes[6] != 0x1A || bytes[7] != 0xE1)
        {
            return new AuthenticodeValidationResult(false, null, null, "Arquivo MSI não é um documento estruturado OLE válido.");
        }

        // Procura pelo OID de SignedData (1.2.840.113549.1.7.2) no arquivo MSI
        var oidIndex = IndexOfBytes(bytes, Pkcs7SignedDataOid);
        if (oidIndex < 0)
        {
            return new AuthenticodeValidationResult(false, null, null, "Instaladores sem assinatura digital ficam fora do catálogo na v1.");
        }

        // Encontra o início da SEQUENCE ASN.1 (0x30) anterior ao OID
        var seqStart = -1;
        for (var i = oidIndex - 1; i >= Math.Max(0, oidIndex - 32); i--)
        {
            if (bytes[i] == 0x30)
            {
                seqStart = i;
                break;
            }
        }

        if (seqStart < 0)
        {
            return new AuthenticodeValidationResult(false, null, null, "Estrutura PKCS#7 da assinatura não identificada no arquivo MSI.");
        }

        try
        {
            // Tenta decodificar o bloco a partir de seqStart até o final
            var pkcsBytes = new byte[bytes.Length - seqStart];
            Buffer.BlockCopy(bytes, seqStart, pkcsBytes, 0, pkcsBytes.Length);
            return ExtractSignerFromPkcs7(pkcsBytes, expectedPublisher);
        }
        catch
        {
            return new AuthenticodeValidationResult(false, null, null, "Assinatura digital Authenticode do arquivo MSI corrompida.");
        }
    }

    private static AuthenticodeValidationResult ExtractSignerFromPkcs7(byte[] pkcs7Bytes, string? expectedPublisher)
    {
        try
        {
            var signedCms = new SignedCms();
            signedCms.Decode(pkcs7Bytes);

            X509Certificate2? signerCert = null;
            if (signedCms.SignerInfos.Count > 0)
            {
                signerCert = signedCms.SignerInfos[0].Certificate;
            }

            if (signerCert == null && signedCms.Certificates.Count > 0)
            {
                signerCert = signedCms.Certificates[0];
            }

            if (signerCert == null)
            {
                return new AuthenticodeValidationResult(false, null, null, "Nenhum certificado de assinante encontrado no pacote Authenticode.");
            }

            var subject = signerCert.Subject;
            var publisher = signerCert.GetNameInfo(X509NameType.SimpleName, false) ?? subject;

            if (!string.IsNullOrWhiteSpace(expectedPublisher) &&
                !publisher.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase) &&
                !subject.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase))
            {
                return new AuthenticodeValidationResult(true, publisher, subject,
                    $"Fabricante da assinatura digital ('{publisher}') não confere com o fabricante esperado cadastrado ('{expectedPublisher}').");
            }

            return new AuthenticodeValidationResult(true, publisher, subject, null);
        }
        catch (Exception ex)
        {
            return new AuthenticodeValidationResult(false, null, null, $"Falha ao decodificar assinatura Authenticode: {ex.Message}");
        }
    }

    private static int IndexOfBytes(byte[] source, byte[] pattern)
    {
        if (source.Length < pattern.Length) return -1;
        for (var i = 0; i <= source.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}
