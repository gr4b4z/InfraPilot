using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// The sign-off state of one work item instance, derived from its <see cref="WorkItemApproval"/> rows
/// with the gate's precedence: a block outranks an issue, either outranks a sibling approval, and an
/// item nobody has ruled on is pending.
/// </summary>
public enum WorkItemInstanceState
{
    Pending,
    Approved,
    Issue,
    Blocked,
}

/// <summary>
/// A ticket in one <c>(product, targetEnv)</c> — the thing that has <i>instances</i>, one per service
/// whose promotions carried it. Not a work item identity by itself (that needs the service); this is
/// the grain the overall status is computed at.
/// </summary>
public readonly record struct WorkItemTicketId(string Key, string Product, string TargetEnv);

/// <summary>One service's instance of a ticket, with its own sign-off state.</summary>
public record WorkItemInstanceStatusView(string Service, string? Title, WorkItemInstanceState State);

/// <summary>
/// What "is MPT-1 done?" means when MPT-1 ships in three services: the roll-up of every instance's
/// state in one <c>(product, targetEnv)</c>. Every instance page and queue row shows it beside the
/// instance's own state, so a manager reading the ticket and a tester signing off one service look at
/// the same object from two sides.
///
/// <para><see cref="State"/> follows the gate's precedence across instances: any block → Blocked, else
/// any issue → Issue, else every instance approved → Approved, else Pending. A promotion whose policy
/// sets <see cref="ResolvedPolicySnapshot.RequireAllWorkItemInstancesApproved"/> waits for this to be
/// Approved rather than for its own service's instance alone.</para>
///
/// <para>Instances are the services with a <see cref="PromotionWorkItem"/> row for the ticket in that
/// product/env — whatever the carrying promotion's status. A service whose promotion already shipped
/// still counts (it was approved to get there); a service whose promotion died without a replacement
/// still counts too, and holds the roll-up until somebody signs it off or the orphan sweep does.</para>
/// </summary>
public record WorkItemOverallStatus(
    WorkItemInstanceState State,
    int Instances,
    int Approved,
    int Issues,
    int Blocked,
    int Pending,
    IReadOnlyList<WorkItemInstanceStatusView> InstanceStatuses)
{
    /// <summary>
    /// The wire shape: states as their names, so the JSON is self-describing wherever this is
    /// embedded (queue rows, detail, the instances resolver).
    /// </summary>
    public WorkItemOverallSummary ToSummary() => new(
        State.ToString(), Instances, Approved, Issues, Blocked, Pending,
        InstanceStatuses.Select(i => new WorkItemInstanceSummary(i.Service, i.Title, i.State.ToString())).ToList());

    /// <summary>The state of one instance from its decision rows.</summary>
    public static WorkItemInstanceState StateOf(IEnumerable<WorkItemDecision> decisions)
    {
        var state = WorkItemInstanceState.Pending;
        foreach (var d in decisions)
        {
            switch (d)
            {
                case WorkItemDecision.Blocked:
                    return WorkItemInstanceState.Blocked;
                case WorkItemDecision.Issue:
                    state = WorkItemInstanceState.Issue;
                    break;
                case WorkItemDecision.Approved when state == WorkItemInstanceState.Pending:
                    state = WorkItemInstanceState.Approved;
                    break;
            }
        }
        return state;
    }

    public static WorkItemOverallStatus Aggregate(IReadOnlyList<WorkItemInstanceStatusView> instances)
    {
        var approved = instances.Count(i => i.State == WorkItemInstanceState.Approved);
        var issues = instances.Count(i => i.State == WorkItemInstanceState.Issue);
        var blocked = instances.Count(i => i.State == WorkItemInstanceState.Blocked);
        var pending = instances.Count(i => i.State == WorkItemInstanceState.Pending);
        var state = blocked > 0 ? WorkItemInstanceState.Blocked
            : issues > 0 ? WorkItemInstanceState.Issue
            : instances.Count > 0 && approved == instances.Count ? WorkItemInstanceState.Approved
            : WorkItemInstanceState.Pending;
        return new WorkItemOverallStatus(state, instances.Count, approved, issues, blocked, pending, instances);
    }

    /// <summary>
    /// Resolves the roll-up for a batch of tickets in two queries — one over the ticket index for the
    /// instances, one over the decisions — so a queue of a hundred rows costs the same as one. Tickets
    /// with no instance in the index are absent from the result.
    /// </summary>
    public static async Task<Dictionary<WorkItemTicketId, WorkItemOverallStatus>> LoadAsync(
        PlatformDbContext db, IEnumerable<WorkItemTicketId> tickets, CancellationToken ct)
    {
        var wanted = tickets
            .Where(t => t.Key.Length > 0 && t.Product.Length > 0 && t.TargetEnv.Length > 0)
            .ToHashSet();
        if (wanted.Count == 0) return new();

        var keys = wanted.Select(t => t.Key).Distinct().ToList();
        var products = wanted.Select(t => t.Product).Distinct().ToList();
        var envs = wanted.Select(t => t.TargetEnv).Distinct().ToList();

        // Coarse IN over the cross product, exact match in memory — the same shape the queue uses.
        // Blank-service rows are pre-migration leftovers with no candidate behind them and are not an
        // instance anybody can act on.
        var rows = await db.PromotionWorkItems.AsNoTracking()
            .Where(w => keys.Contains(w.WorkItemKey) && products.Contains(w.Product)
                     && envs.Contains(w.TargetEnv) && w.Service != "")
            .Select(w => new { w.WorkItemKey, w.Product, w.TargetEnv, w.Service, w.Title, w.CreatedAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return new();

        var decisions = await db.WorkItemApprovals.AsNoTracking()
            .Where(a => keys.Contains(a.WorkItemKey) && products.Contains(a.Product) && envs.Contains(a.TargetEnv))
            .Select(a => new { a.WorkItemKey, a.Product, a.TargetEnv, a.Service, a.Decision })
            .ToListAsync(ct);
        var decisionsByInstance = decisions
            .GroupBy(d => (new WorkItemTicketId(d.WorkItemKey, d.Product, d.TargetEnv), d.Service))
            .ToDictionary(g => g.Key, g => g.Select(d => d.Decision).ToList());

        var result = new Dictionary<WorkItemTicketId, WorkItemOverallStatus>();
        foreach (var group in rows.GroupBy(r => new WorkItemTicketId(r.WorkItemKey, r.Product, r.TargetEnv)))
        {
            if (!wanted.Contains(group.Key)) continue;
            var instances = group
                .GroupBy(r => r.Service, StringComparer.Ordinal)
                .Select(g => new WorkItemInstanceStatusView(
                    Service: g.Key,
                    // The newest candidate's title, so a renamed ticket reads by its current name.
                    Title: g.OrderByDescending(r => r.CreatedAt)
                        .Select(r => r.Title).FirstOrDefault(t => !string.IsNullOrEmpty(t)),
                    State: StateOf(decisionsByInstance.GetValueOrDefault((group.Key, g.Key)) ?? new())))
                .OrderBy(i => i.Service, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result[group.Key] = Aggregate(instances);
        }
        return result;
    }

    /// <summary>Single-ticket convenience over <see cref="LoadAsync"/>. Null when the ticket has no instances.</summary>
    public static async Task<WorkItemOverallStatus?> LoadOneAsync(
        PlatformDbContext db, string key, string product, string targetEnv, CancellationToken ct)
    {
        var id = new WorkItemTicketId(key, product, targetEnv);
        return (await LoadAsync(db, new[] { id }, ct)).GetValueOrDefault(id);
    }
}

/// <summary>JSON shape of <see cref="WorkItemOverallStatus"/> — see <see cref="WorkItemOverallStatus.ToSummary"/>.</summary>
public record WorkItemOverallSummary(
    string State,
    int Instances,
    int Approved,
    int Issues,
    int Blocked,
    int Pending,
    IReadOnlyList<WorkItemInstanceSummary> InstanceStatuses);

/// <summary>JSON shape of <see cref="WorkItemInstanceStatusView"/>.</summary>
public record WorkItemInstanceSummary(string Service, string? Title, string State);
