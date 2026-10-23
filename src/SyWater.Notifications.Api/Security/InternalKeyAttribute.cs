using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SyWater.Notifications.Api.Security;

/// <summary>
/// Service-to-service endpoints (same filter as ms-devices): no user token, the shared X-Internal-Key is the credential.
/// Compared in constant time. Wrong or missing key = 403 auth.internal_only.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class InternalKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string Header = "X-Internal-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Internal:ApiKey"];
        var received = context.HttpContext.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(expected) || !FixedTimeEquals(expected, received))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "auth.internal_only",
                Detail = "This endpoint can only be called by another Sy Water service.",
            }) { StatusCode = StatusCodes.Status403Forbidden };
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
