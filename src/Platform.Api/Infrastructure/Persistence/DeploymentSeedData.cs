using System.Text.Json;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Infrastructure.Persistence;

public static class DeploymentSeedData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task Seed(PlatformDbContext db)
    {
        if (db.DeployEvents.Any()) return;

        var now = DateTimeOffset.UtcNow;
        var events = new List<DeployEvent>();

        // ──────────────────────────────────────────────
        // ticketing-platform: orders
        // ──────────────────────────────────────────────

        events.Add(MakeEvent("ticketing-platform", "orders", "production", "2.3.0", "2.2.8",
            "webhook", now.AddDays(-7),
            [new ReferenceDto("pipeline", "https://dev.azure.com/softwareone-pc/MPT/_build/results?buildId=12300"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1234", "jira", "PLAT-1234"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/orders/pullrequest/542")],
            [new ParticipantDto("PR Author", "Jan Kowalski", "jan.kowalski@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Anna Kowalska", "anna.kowalska@softwareone.com"),
             new ParticipantDto("QA", "Marta Wiśniewska", "marta.wisniewska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Fix order total calculation for multi-currency baskets",
                ["workItemStatus"] = "Done",
                ["prTitle"] = "fix: correct currency conversion in order totals",
            }, [])));

        events.Add(MakeEvent("ticketing-platform", "orders", "staging", "2.3.1", "2.3.0",
            "webhook", now.AddDays(-4),
            [new ReferenceDto("pipeline", "https://dev.azure.com/softwareone-pc/MPT/_build/results?buildId=12335"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1280", "jira", "PLAT-1280"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/orders/pullrequest/558")],
            [new ParticipantDto("PR Author", "Jan Kowalski", "jan.kowalski@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Piotr Nowak", "piotr.nowak@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Add pagination to order history endpoint",
                ["workItemStatus"] = "In Review",
                ["prTitle"] = "feat: paginated order history with cursor-based navigation",
            }, [])));

        events.Add(MakeEvent("ticketing-platform", "orders", "development", "2.4.0", "2.3.1",
            "webhook", now.AddDays(-2),
            [new ReferenceDto("pipeline", "https://dev.azure.com/softwareone-pc/MPT/_build/results?buildId=12340"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1301", "jira", "PLAT-1301"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/orders/pullrequest/563")],
            [new ParticipantDto("PR Author", "Jan Kowalski", "jan.kowalski@softwareone.com"),
             new ParticipantDto("QA", "Marta Wiśniewska", "marta.wisniewska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Implement order cancellation with refund workflow",
                ["workItemStatus"] = "In Progress",
                ["prTitle"] = "feat: order cancellation API with automatic refund trigger",
            }, [])));

        // orders — deployed today to dev
        events.Add(MakeEvent("ticketing-platform", "orders", "development", "2.4.1", "2.4.0",
            "webhook", now.AddHours(-3),
            [new ReferenceDto("pipeline", "https://dev.azure.com/softwareone-pc/MPT/_build/results?buildId=12380"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/orders/pullrequest/567"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1315", "jira", "PLAT-1315")],
            [new ParticipantDto("PR Author", "Sylwester Grabowski", "sylwester.grabowski@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Jan Kowalski", "jan.kowalski@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Add webhook retry with exponential backoff for payment notifications",
                ["workItemStatus"] = "In Progress",
                ["prTitle"] = "feat: exponential backoff for payment webhook retries",
            }, [])));

        // orders — promoted to staging today
        events.Add(MakeEvent("ticketing-platform", "orders", "staging", "2.4.0", "2.3.1",
            "webhook", now.AddHours(-1),
            [new ReferenceDto("pipeline", "https://dev.azure.com/softwareone-pc/MPT/_build/results?buildId=12385"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1301", "jira", "PLAT-1301"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/orders/pullrequest/563")],
            [new ParticipantDto("PR Author", "Jan Kowalski", "jan.kowalski@softwareone.com"),
             new ParticipantDto("QA", "Marta Wiśniewska", "marta.wisniewska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Implement order cancellation with refund workflow",
                ["workItemStatus"] = "In Review",
                ["prTitle"] = "feat: order cancellation API with automatic refund trigger",
            }, [])));

        // ──────────────────────────────────────────────
        // ticketing-platform: schedule
        // ──────────────────────────────────────────────

        events.Add(MakeEvent("ticketing-platform", "schedule", "production", "1.7.1", "1.7.0",
            "k8s-observer", now.AddDays(-6),
            [new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1190", "jira", "PLAT-1190"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/schedule/pullrequest/320")],
            [new ParticipantDto("PR Author", "Marta Wiśniewska", "marta.wisniewska@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Jan Kowalski", "jan.kowalski@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Fix timezone handling in recurring event schedules",
                ["workItemStatus"] = "Done",
                ["prTitle"] = "fix: use UTC offset for recurring schedule calculations",
            }, [])));

        events.Add(MakeEvent("ticketing-platform", "schedule", "staging", "1.7.2", "1.7.1",
            "k8s-observer", now.AddDays(-3),
            [new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1245", "jira", "PLAT-1245"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/schedule/pullrequest/331")],
            [new ParticipantDto("PR Author", "Anna Kowalska", "anna.kowalska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Add bulk schedule import from CSV",
                ["workItemStatus"] = "In Review",
                ["prTitle"] = "feat: CSV import endpoint for bulk schedule creation",
            }, [])));

        events.Add(MakeEvent("ticketing-platform", "schedule", "development", "1.8.0", "1.7.2",
            "k8s-observer", now.AddDays(-1),
            [new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1298", "jira", "PLAT-1298"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/schedule/pullrequest/345"),
             new ReferenceDto("repository", "https://dev.azure.com/softwareone-pc/MPT/_git/schedule", Revision: "f8e2a1c9d4b7")],
            [new ParticipantDto("PR Author", "Piotr Nowak", "piotr.nowak@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Marta Wiśniewska", "marta.wisniewska@softwareone.com"),
             new ParticipantDto("QA", "Anna Kowalska", "anna.kowalska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Implement schedule conflict detection and resolution",
                ["workItemStatus"] = "In Progress",
                ["prTitle"] = "feat: conflict detection engine for overlapping schedules",
            }, [])));

        // ──────────────────────────────────────────────
        // ticketing-platform: billing
        // ──────────────────────────────────────────────

        events.Add(MakeEvent("ticketing-platform", "billing", "production", "3.0.4", "3.0.3",
            "webhook", now.AddDays(-8),
            [new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1150", "jira", "PLAT-1150"),
             new ReferenceDto("pipeline", "https://dev.azure.com/softwareone-pc/MPT/_build/results?buildId=12210"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/billing/pullrequest/410")],
            [new ParticipantDto("PR Author", "Anna Kowalska", "anna.kowalska@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Sylwester Grabowski", "sylwester.grabowski@softwareone.com"),
             new ParticipantDto("QA", "Jan Kowalski", "jan.kowalski@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Fix invoice PDF generation for VAT-exempt transactions",
                ["workItemStatus"] = "Done",
                ["prTitle"] = "fix: skip VAT line items in PDF when tax-exempt flag is set",
            }, [])));

        events.Add(MakeEvent("ticketing-platform", "billing", "staging", "3.0.5", "3.0.4",
            "webhook", now.AddDays(-3),
            [new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1220", "jira", "PLAT-1220"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/billing/pullrequest/425")],
            [new ParticipantDto("PR Author", "Piotr Nowak", "piotr.nowak@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Anna Kowalska", "anna.kowalska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Add Stripe Connect integration for marketplace payouts",
                ["workItemStatus"] = "In Review",
                ["prTitle"] = "feat: Stripe Connect payout flow for seller accounts",
            }, [])));

        events.Add(MakeEvent("ticketing-platform", "billing", "development", "3.1.0", "3.0.5",
            "webhook", now.AddHours(-5),
            [new ReferenceDto("repository", "https://dev.azure.com/softwareone-pc/MPT/_git/billing", Revision: "a1b2c3d4e5f6"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/PLAT-1310", "jira", "PLAT-1310"),
             new ReferenceDto("pull-request", "https://dev.azure.com/softwareone-pc/MPT/_git/billing/pullrequest/440")],
            [new ParticipantDto("PR Author", "Anna Kowalska", "anna.kowalska@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Piotr Nowak", "piotr.nowak@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Implement usage-based billing metering API",
                ["workItemStatus"] = "In Progress",
                ["prTitle"] = "feat: metering endpoint for tracking API usage per subscription",
            }, [])));

        // ──────────────────────────────────────────────
        // marketplace: marketplace-api
        // ──────────────────────────────────────────────

        events.Add(MakeEvent("marketplace", "marketplace-api", "production", "5.1.2", "5.1.1",
            "github-actions", now.AddDays(-10),
            [new ReferenceDto("pipeline", "https://github.com/softwareone/marketplace-api/actions/runs/97200"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/MKT-389", "jira", "MKT-389"),
             new ReferenceDto("pull-request", "https://github.com/softwareone/marketplace-api/pull/201")],
            [new ParticipantDto("PR Author", "Piotr Nowak", "piotr.nowak@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Sylwester Grabowski", "sylwester.grabowski@softwareone.com"),
             new ParticipantDto("QA", "Marta Wiśniewska", "marta.wisniewska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Fix product search returning stale results after catalog update",
                ["workItemStatus"] = "Done",
                ["prTitle"] = "fix: invalidate search index cache on catalog write",
            }, [])));

        events.Add(MakeEvent("marketplace", "marketplace-api", "staging", "5.1.3", "5.1.2",
            "github-actions", now.AddDays(-3),
            [new ReferenceDto("pipeline", "https://github.com/softwareone/marketplace-api/actions/runs/98100"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/MKT-420", "jira", "MKT-420"),
             new ReferenceDto("pull-request", "https://github.com/softwareone/marketplace-api/pull/215")],
            [new ParticipantDto("PR Author", "Sylwester Grabowski", "sylwester.grabowski@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Piotr Nowak", "piotr.nowak@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Implement vendor onboarding API with document verification",
                ["workItemStatus"] = "In Review",
                ["prTitle"] = "feat: vendor registration flow with KYC document upload",
            }, [])));

        events.Add(MakeEvent("marketplace", "marketplace-api", "development", "5.2.0", "5.1.3",
            "github-actions", now.AddHours(-2),
            [new ReferenceDto("pipeline", "https://github.com/softwareone/marketplace-api/actions/runs/98765"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/MKT-456", "jira", "MKT-456"),
             new ReferenceDto("pull-request", "https://github.com/softwareone/marketplace-api/pull/230")],
            [new ParticipantDto("PR Author", "Piotr Nowak", "piotr.nowak@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Anna Kowalska", "anna.kowalska@softwareone.com"),
             new ParticipantDto("QA", "Jan Kowalski", "jan.kowalski@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Add product comparison API with feature matrix",
                ["workItemStatus"] = "In Progress",
                ["prTitle"] = "feat: comparison endpoint returning normalized feature matrix",
            }, [])));

        // marketplace-api — promoted to production today
        events.Add(MakeEvent("marketplace", "marketplace-api", "production", "5.1.3", "5.1.2",
            "github-actions", now.AddMinutes(-45),
            [new ReferenceDto("pipeline", "https://github.com/softwareone/marketplace-api/actions/runs/98900"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/MKT-420", "jira", "MKT-420"),
             new ReferenceDto("pull-request", "https://github.com/softwareone/marketplace-api/pull/215")],
            [new ParticipantDto("PR Author", "Sylwester Grabowski", "sylwester.grabowski@softwareone.com"),
             new ParticipantDto("QA", "Marta Wiśniewska", "marta.wisniewska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Implement vendor onboarding API with document verification",
                ["workItemStatus"] = "Done",
                ["prTitle"] = "feat: vendor registration flow with KYC document upload",
            }, [])));

        // ──────────────────────────────────────────────
        // marketplace: marketplace-ui
        // ──────────────────────────────────────────────

        events.Add(MakeEvent("marketplace", "marketplace-ui", "production", "1.9.7", "1.9.6",
            "github-actions", now.AddDays(-12),
            [new ReferenceDto("pipeline", "https://github.com/softwareone/marketplace-ui/actions/runs/45100"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/MKT-370", "jira", "MKT-370"),
             new ReferenceDto("pull-request", "https://github.com/softwareone/marketplace-ui/pull/180")],
            [new ParticipantDto("PR Author", "Marta Wiśniewska", "marta.wisniewska@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Sylwester Grabowski", "sylwester.grabowski@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Fix mobile layout breakpoints on product detail page",
                ["workItemStatus"] = "Done",
                ["prTitle"] = "fix: responsive grid breakpoints for product detail cards",
            }, [])));

        events.Add(MakeEvent("marketplace", "marketplace-ui", "staging", "1.9.8", "1.9.7",
            "github-actions", now.AddDays(-5),
            [new ReferenceDto("pipeline", "https://github.com/softwareone/marketplace-ui/actions/runs/46200"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/MKT-410", "jira", "MKT-410"),
             new ReferenceDto("pull-request", "https://github.com/softwareone/marketplace-ui/pull/195")],
            [new ParticipantDto("PR Author", "Jan Kowalski", "jan.kowalski@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Marta Wiśniewska", "marta.wisniewska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Add dark mode support with system preference detection",
                ["workItemStatus"] = "In Review",
                ["prTitle"] = "feat: dark mode toggle with prefers-color-scheme sync",
            }, [])));

        events.Add(MakeEvent("marketplace", "marketplace-ui", "development", "2.0.0-beta.3", "2.0.0-beta.2",
            "github-actions", now.AddHours(-4),
            [new ReferenceDto("pipeline", "https://github.com/softwareone/marketplace-ui/actions/runs/47500"),
             new ReferenceDto("work-item", "https://softwareone.atlassian.net/browse/MKT-460", "jira", "MKT-460"),
             new ReferenceDto("pull-request", "https://github.com/softwareone/marketplace-ui/pull/210"),
             new ReferenceDto("repository", "https://github.com/softwareone/marketplace-ui", Revision: "b3c4d5e6f7a8")],
            [new ParticipantDto("PR Author", "Sylwester Grabowski", "sylwester.grabowski@softwareone.com"),
             new ParticipantDto("PR Reviewer", "Piotr Nowak", "piotr.nowak@softwareone.com"),
             new ParticipantDto("QA", "Anna Kowalska", "anna.kowalska@softwareone.com")],
            new EnrichmentData(new Dictionary<string, string>
            {
                ["workItemTitle"] = "Rebuild checkout flow with multi-step wizard and validation",
                ["workItemStatus"] = "In Progress",
                ["prTitle"] = "feat: multi-step checkout wizard with real-time validation",
            }, [])));

        db.DeployEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private record EnrichmentData(Dictionary<string, string> Labels, List<ParticipantDto> Participants);

    private static DeployEvent MakeEvent(
        string product, string service, string environment, string version, string? previousVersion,
        string source, DateTimeOffset deployedAt,
        List<ReferenceDto> references, List<ParticipantDto> participants,
        EnrichmentData? enrichment = null)
    {
        string? enrichmentJson = null;
        if (enrichment is not null)
        {
            enrichmentJson = JsonSerializer.Serialize(new
            {
                labels = enrichment.Labels,
                participants = enrichment.Participants,
                enrichedAt = deployedAt,
            }, JsonOptions);
        }

        return new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = environment,
            Version = version,
            PreviousVersion = previousVersion,
            Source = source,
            DeployedAt = deployedAt,
            ReferencesJson = JsonSerializer.Serialize(references, JsonOptions),
            ParticipantsJson = JsonSerializer.Serialize(participants, JsonOptions),
            EnrichmentJson = enrichmentJson,
            MetadataJson = "{}",
            CreatedAt = deployedAt,
        };
    }
}
