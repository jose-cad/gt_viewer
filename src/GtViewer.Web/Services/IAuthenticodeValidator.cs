namespace GtViewer.Web.Services;

public record AuthenticodeValidationResult(
    bool IsSigned,
    string? Publisher,
    string? Subject,
    string? ErrorMessage
);

public interface IAuthenticodeValidator
{
    AuthenticodeValidationResult ValidateFile(string filePath, string? expectedPublisher = null);
}
