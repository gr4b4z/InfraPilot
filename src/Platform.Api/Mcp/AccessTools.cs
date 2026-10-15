using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Mcp;

[McpServerToolType]
public sealed class AccessTools(McpContext ctx, ICurrentUser user)
{
    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The identity this connection acts as and what it may do: roles, the products a key is limited to, " +
                 "its scopes, and whether it can use the pipeline-only tools. Call it when a write fails with a " +
                 "permission error, or before acting on someone's behalf.")]
    public string WhoAmI()
    {
        var principal = ctx.User;
        var method = principal.FindFirstValue("auth_method") ?? "bearer";
        var scopes = principal.FindAll(ApiKeyAuthHandler.ScopeClaim).Select(c => c.Value).ToList();

        return ctx.Result(new
        {
            name = user.Name,
            email = string.IsNullOrEmpty(user.Email) ? null : user.Email,
            authMethod = method,
            roles = user.Roles,
            isAdmin = user.IsAdmin,
            allowedProducts = ctx.AllowedProducts.Count == 0 ? null : ctx.AllowedProducts,
            scopes = scopes.Count == 0 ? null : scopes,
            expiresAt = principal.FindFirstValue(ApiKeyAuthHandler.ExpiresAtClaim),
            can = new
            {
                // POST /api/promotions takes a configured pipeline key, scoped for it.
                createPromotion = ctx.IsPipelineKey && ApiKeyAuthHandler.HasScope(principal, ApiKeyScopes.PromotionCreate),
                promoteBuild = true,
                comment = true,
                approve = false,
            },
            portal = ctx.Url(""),
            note = method switch
            {
                "personal_token" => "A personal token acts as the person who created it, without admin rights, and cannot use pipeline-only actions.",
                "api_key" => "A configured API key acts as itself (shown as its name). It is product-limited when allowedProducts is set.",
                _ => null,
            },
        });
    }
}
