namespace Platform.Api.Infrastructure.Auth;

public class LocalUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string RolesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Computed accessor — not persisted
    public List<string> Roles
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(RolesJson) ?? [];
        set => RolesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}
