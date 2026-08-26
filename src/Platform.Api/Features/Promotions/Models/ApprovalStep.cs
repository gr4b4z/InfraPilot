using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// An AD group reference on an <see cref="ApproverRequirement"/>. Carries both the directory object
/// <see cref="Id"/> (what Graph/group-claim membership keys off) and the human-readable
/// <see cref="Name"/> (what the editor renders and role claims usually carry). Approval matching
/// checks both, so a group qualifies whether the user's claims expose the id or the name.
///
/// <para>Serialised as an object <c>{ "id", "name" }</c>. For backward compatibility the converter
/// also reads a bare JSON string (legacy data stored just the group string), treating it as both id
/// and name — so existing <c>"groups":["InfraPortal.Admin"]</c> rows still deserialise without a DB
/// migration.</para>
/// </summary>
[JsonConverter(typeof(GroupRefJsonConverter))]
public record GroupRef(string Id, string Name)
{
    public GroupRef() : this("", "") { }
}

/// <summary>
/// Backward-tolerant converter for <see cref="GroupRef"/>. Works under any
/// <see cref="JsonSerializerOptions"/> because it is attached via <see cref="JsonConverterAttribute"/>
/// on the record itself.
/// <list type="bullet">
/// <item>Read string <c>"X"</c> ⇒ <c>GroupRef("X", "X")</c> (legacy bare-string data).</item>
/// <item>Read object <c>{ "id": "...", "name": "..." }</c> (camelCase); missing name defaults to id.</item>
/// <item>Write object <c>{ "id", "name" }</c> (camelCase).</item>
/// </list>
/// </summary>
public sealed class GroupRefJsonConverter : JsonConverter<GroupRef>
{
    public override GroupRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            return new GroupRef(s, s);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? id = null;
            string? name = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var prop = reader.GetString();
                reader.Read();
                if (string.Equals(prop, "id", StringComparison.OrdinalIgnoreCase))
                    id = reader.GetString();
                else if (string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase))
                    name = reader.GetString();
                else
                    reader.Skip();
            }

            var resolvedId = id ?? name ?? "";
            return new GroupRef(resolvedId, name ?? resolvedId);
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when reading GroupRef.");
    }

    public override void Write(Utf8JsonWriter writer, GroupRef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}

/// <summary>
/// A bounded rule tree for promotion approval. A <see cref="PromotionPolicy"/> carries a list of
/// <see cref="ApprovalStep"/>s; each step a list of <see cref="ApproverRequirement"/>s. The gate
/// is satisfied when <b>every</b> requirement across <b>every</b> step is satisfied (parallel AND
/// over the flattened requirement set — steps are an organisational grouping for the admin UI, not
/// a sequencing constraint, per plan §8).
///
/// <para>These records are serialised onto <see cref="PromotionPolicy.ApprovalStepsJson"/> and,
/// transitively, onto <see cref="ResolvedPolicySnapshot"/> (and thus
/// <see cref="PromotionCandidate.ResolvedPolicyJson"/>). They use init-only positional members with
/// sensible defaults so older snapshot JSON deserialises cleanly.</para>
/// </summary>
public record ApprovalStep(string Name, List<ApproverRequirement> Requirements)
{
    public ApprovalStep() : this("", new()) { }
}

/// <summary>
/// One requirement within an <see cref="ApprovalStep"/>. Satisfiable by anyone in the union of
/// <see cref="Groups"/> ∪ <see cref="Users"/>. Satisfied once at least <see cref="MinApprovers"/>
/// <b>distinct</b> eligible people have approved the candidate.
///
/// <para>There is intentionally no <c>Strategy</c> field — <see cref="MinApprovers"/> subsumes it:
/// "any one approver" is <c>MinApprovers = 1</c>; "N of M" is <c>MinApprovers = N</c> (plan §8,
/// decision D6).</para>
///
/// <para><see cref="Groups"/> entries are matched the same way the legacy single approver group was:
/// role claim, Entra group object id, or live Graph membership. <see cref="Users"/> entries are
/// matched case-insensitively against the approver's email.</para>
/// </summary>
public record ApproverRequirement(
    string Name,
    List<GroupRef> Groups,
    List<string> Users,
    int MinApprovers = 1)
{
    public ApproverRequirement() : this("", new(), new(), 1) { }
}
