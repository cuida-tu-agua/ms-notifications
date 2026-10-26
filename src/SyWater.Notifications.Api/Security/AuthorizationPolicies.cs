namespace SyWater.Notifications.Api.Security;

public static class AuthorizationPolicies
{
    /// <summary>The token has the ADMIN role (claim "roles" of ms-iam). A blocked or demoted admin loses it with the token.</summary>
    public const string Admin = "Admin";
    public const string AdminRole = "ADMIN";
}
