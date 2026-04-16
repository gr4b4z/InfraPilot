using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Auth;

public static class LocalAuthEndpoints
{
    public const string Issuer = "platform-local";
    public const string Audience = "platform-local";
    public const string DefaultDevKey = "local-dev-jwt-signing-key-min-32!";

    public static RouteGroupBuilder MapLocalAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/login", async (
            PlatformDbContext db,
            IConfiguration config,
            LoginRequest request) =>
        {
            var user = await db.LocalUsers.FirstOrDefaultAsync(u => u.Email == request.Email && u.IsActive);
            if (user is null)
                return Results.Unauthorized();

            var hasher = new PasswordHasher<LocalUser>();
            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            if (result == PasswordVerificationResult.Failed)
                return Results.Unauthorized();

            var signingKey = config["Auth:LocalJwt:Key"] ?? DefaultDevKey;
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new("name", user.Name),
                new("preferred_username", user.Email),
            };
            foreach (var role in user.Roles)
                claims.Add(new Claim("roles", role));

            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(24),
                signingCredentials: creds);

            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

            return Results.Ok(new
            {
                token = tokenString,
                user = new
                {
                    id = user.Id.ToString(),
                    name = user.Name,
                    email = user.Email,
                    roles = user.Roles,
                    isAdmin = user.Roles.Contains("InfraPortal.Admin"),
                    isQA = user.Roles.Contains("InfraPortal.QA"),
                },
            });
        }).AllowAnonymous();

        group.MapGet("/me", (ICurrentUser currentUser) =>
        {
            return Results.Ok(new
            {
                id = currentUser.Id,
                name = currentUser.Name,
                email = currentUser.Email,
                roles = currentUser.Roles,
                isAdmin = currentUser.IsAdmin,
                isQA = currentUser.IsQA,
            });
        });

        return group;
    }
}

public record LoginRequest(string Email, string Password);
