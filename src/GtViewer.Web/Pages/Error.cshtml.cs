using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    public bool IsAccessDenied => Code == "403" || HttpContext.Response.StatusCode == 403;

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        if (IsAccessDenied)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }
}

