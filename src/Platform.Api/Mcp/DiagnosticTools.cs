using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Agent;
using Platform.Api.Features.Diagnostics;
using Platform.Api.Features.Knowledge;

namespace Platform.Api.Mcp;

/// <summary>
/// The diagnosis playbooks and the platform knowledge base — what the in-app assistant answers "why"
/// questions from, and what REST does not expose. A diagnosis gathers the evidence the playbooks key
/// on (deploys, approvals, webhook deliveries, logs) and ranks the causes they list, so an agent
/// starts from the known failure modes of these pipelines instead of re-deriving them from raw rows.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools(McpContext ctx, DiagnosticsService diagnostics, KnowledgeRegistry knowledge)
{
    [McpServerTool(Name = "diagnose_promotion", Title = "Diagnose a promotion", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains why a promotion is where it is — stuck Pending, Approved but never deployed, and so on: what should " +
                 "happen next, the evidence gathered, and the probable causes from the platform's playbooks, most likely first, " +
                 "each with how to confirm and fix it. Call this before reasoning from raw promotion data.")]
    public async Task<string> DiagnosePromotion(
        [Description("Promotion id (GUID).")] string promotionId,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(promotionId, "promotion", "list_promotions");
        var d = await diagnostics.DiagnosePromotionAsync(id, ct) ?? throw new McpException($"No promotion with id {id}.");

        return ctx.Result(new
        {
            promotion = new
            {
                id = d.CandidateId,
                d.Product,
                d.Service,
                edge = $"{d.SourceEnv} → {d.TargetEnv}",
                d.Version,
                d.Status,
                d.ApprovedAt,
                ageHours = Math.Round(d.Age.TotalHours, 1),
                url = ctx.Url(PortalRoutes.Promotion(d.CandidateId)),
            },
            whatShouldHappen = d.SymptomSummary,
            evidence = d.Evidence,
            observations = d.Observations,
            probableCauses = Causes(d.ProbableCauses),
            note = CausesNote,
        });
    }

    [McpServerTool(Name = "diagnose_deployment", Title = "Diagnose a deployment", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains a failed (or odd) deploy event: the evidence, the log lines that matter, and the probable causes from the " +
                 "platform's playbooks with how to confirm and fix each. Quote log excerpts verbatim when reporting.")]
    public async Task<string> DiagnoseDeployment(
        [Description("Deploy event id (GUID).")] string eventId,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(eventId, "deploy event", "get_deployment_history or list_recent_deployments");
        var d = await diagnostics.DiagnoseDeploymentAsync(id, ct) ?? throw new McpException($"No deploy event with id {id}.");

        return ctx.Result(new
        {
            deployment = new
            {
                id = d.EventId,
                d.Product,
                d.Service,
                d.Environment,
                d.Version,
                d.Status,
                d.Source,
                d.DeployedAt,
                url = ctx.Url(PortalRoutes.DeployEvent(d.EventId)),
            },
            whatShouldHappen = d.SymptomSummary,
            evidence = d.Evidence,
            observations = d.Observations,
            logExcerpts = d.LogExcerpts,
            probableCauses = Causes(d.ProbableCauses),
            note = CausesNote,
        });
    }

    [McpServerTool(Name = "search_knowledge", Title = "Search platform knowledge", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the platform knowledge base: how the release pipelines, promotion webhooks, build registration and " +
                 "reconcile jobs of this installation behave. Answer pipeline questions from these topics, stating their as-of date; " +
                 "with no match it lists every topic.")]
    public string SearchKnowledge(
        [Description("What you want to know, e.g. \"promotion approved but not deployed\" or \"how build registration works\".")] string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("`query` is required.");
        // Three topics, bodies clipped: a topic runs to several thousand characters, and past the best
        // few matches the rest are rarely the answer — the agent can search again with sharper words.
        var matches = knowledge.Search(query, limit: 3);
        if (matches.Count == 0)
            return ctx.Result(new
            {
                matches = Array.Empty<object>(),
                note = "No topic matched. These are all the topics; say plainly if none covers the question.",
                available = knowledge.All.Select(t => new { t.Id, t.Title, t.Group, t.Summary }),
            });

        return ctx.Result(new
        {
            matches = matches.Select(m => new
            {
                m.Item.Id,
                m.Item.Title,
                m.Item.Group,
                m.Item.Summary,
                body = McpShapes.Clip(m.Item.Body, 6000),
                m.Item.Source,
                m.Item.AsOf,
                related = m.Item.Related.Count == 0 ? null : m.Item.Related,
            }),
        });
    }

    private const string CausesNote =
        "Causes are ordered most likely first. supportedByEvidence=false means a general possibility the evidence neither " +
        "confirms nor rules out — say so. Lead with the most likely cause and cite the evidence line behind it; do not add " +
        "causes beyond this list.";

    private static IEnumerable<object> Causes(IEnumerable<MatchedCause> causes) => causes.Select(c => (object)new
    {
        c.Cause.Id,
        c.Cause.Title,
        c.Cause.Explanation,
        confirm = c.Cause.Confirm,
        fix = c.Cause.Fix,
        supportedByEvidence = c.Specificity > 0,
    });
}
