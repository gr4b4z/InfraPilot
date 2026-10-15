using Platform.Api.Agent;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Mcp;

/// <summary>
/// The compact shapes MCP tools return. The REST shapes serve pages and pipelines, which want every
/// field; an agent pays for each one in context, and the heavy ones — a ticket's whole description,
/// a reference's nested people, a run's full metadata — are rarely what it asked about. These keep
/// what identifies, links and explains a record, and the detail tools add the rest on request.
/// </summary>
internal static class McpShapes
{
    public static object Event(McpContext ctx, DeployEventResponseDto e) => new
    {
        id = e.Id,
        product = e.Product,
        service = e.Service,
        environment = e.Environment,
        version = e.Version,
        previousVersion = e.PreviousVersion,
        status = e.Status,
        isRollback = e.IsRollback ? true : (bool?)null,
        source = e.Source,
        deployedAt = e.DeployedAt,
        failureReason = e.Run?.FailureReason,
        url = ctx.Url(PortalRoutes.DeployEvent(e.Id)),
    };

    public static object? Run(DeployRun? run) => run is null ? null : new
    {
        run.Provider,
        run.WorkflowName,
        run.JobName,
        run.RunNumber,
        run.Attempt,
        run.RunUrl,
        run.JobUrl,
        run.TriggeredBy,
        run.StartedAt,
        run.CompletedAt,
        run.FailureReason,
    };

    /// <summary>
    /// A reference as a list names it. Work items read through <see cref="WorkItemDisplay"/> first, so
    /// a ticket is named by its summary rather than the commit subject some producers put in Title.
    /// </summary>
    public static IEnumerable<object> References(IEnumerable<ReferenceDto> references) =>
        WorkItemDisplay.ApplyToReferences(references.ToList()).Select(r => (object)new
        {
            type = r.Type,
            key = r.Key,
            title = r.Title,
            subTitle = r.SubTitle,
            revision = r.Revision,
            provider = r.Provider,
            url = r.Url,
        });

    public static IEnumerable<object> People(IEnumerable<ParticipantDto> participants) =>
        participants.Select(p => (object)new { role = p.Role, name = p.DisplayName, email = p.Email });

    public static string? Clip(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + $"… [{text.Length - max} more characters]";
}
