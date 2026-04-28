using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Approvals.Models;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Features;

namespace Platform.Api.Infrastructure.Persistence;

public class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    // Used by the provider-specific subclasses (PostgresPlatformDbContext / SqlServerPlatformDbContext)
    // so each subclass can receive its own DbContextOptions<T> from DI.
    protected PlatformDbContext(DbContextOptions options) : base(options) { }

    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<CatalogItemVersion> CatalogItemVersions => Set<CatalogItemVersion>();
    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();
    public DbSet<FileAttachment> FileAttachments => Set<FileAttachment>();
    public DbSet<ExecutionResult> ExecutionResults => Set<ExecutionResult>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<DeployEvent> DeployEvents => Set<DeployEvent>();
    public DbSet<DeployEventWorkItem> DeployEventWorkItems => Set<DeployEventWorkItem>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();
    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();
    public DbSet<PromotionPolicy> PromotionPolicies => Set<PromotionPolicy>();
    public DbSet<PromotionCandidate> PromotionCandidates => Set<PromotionCandidate>();
    public DbSet<PromotionApproval> PromotionApprovals => Set<PromotionApproval>();
    public DbSet<PromotionComment> PromotionComments => Set<PromotionComment>();
    public DbSet<WorkItemApproval> WorkItemApprovals => Set<WorkItemApproval>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // On SQL Server, `string` columns default to nvarchar(max) which is what we want for JSON payloads.
        // On Postgres we annotate them as `jsonb` for better storage + indexability.
        var jsonType = Database.IsNpgsql() ? "jsonb" : null;

        // Catalog
        modelBuilder.Entity<CatalogItem>(e =>
        {
            e.ToTable("catalog_items");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasMaxLength(50).IsRequired();
            e.Property(x => x.Icon).HasMaxLength(50);
            e.Property(x => x.CurrentYamlHash).HasMaxLength(64).IsRequired();

            // JSON storage columns for catalog definition
            var inputsJson = e.Property(x => x.InputsJson).HasDefaultValue("[]");
            var validationsJson = e.Property(x => x.ValidationsJson).HasDefaultValue("[]");
            var approvalJson = e.Property(x => x.ApprovalJson);
            var executorJson = e.Property(x => x.ExecutorJson);
            if (jsonType != null)
            {
                inputsJson.HasColumnType(jsonType);
                validationsJson.HasColumnType(jsonType);
                approvalJson.HasColumnType(jsonType);
                executorJson.HasColumnType(jsonType);
            }

            // Computed accessors — not persisted
            e.Ignore(x => x.Inputs);
            e.Ignore(x => x.Validations);
            e.Ignore(x => x.Approval);
            e.Ignore(x => x.Executor);
        });

        modelBuilder.Entity<CatalogItemVersion>(e =>
        {
            e.ToTable("catalog_item_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.YamlContent).IsRequired();
            e.Property(x => x.YamlHash).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.CatalogItem)
                .WithMany(x => x.Versions)
                .HasForeignKey(x => x.CatalogItemId);
        });

        // Requests
        modelBuilder.Entity<ServiceRequest>(e =>
        {
            e.ToTable("service_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.RequesterId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RequesterName).HasMaxLength(200).IsRequired();
            e.Property(x => x.RequesterEmail).HasMaxLength(300).HasDefaultValue("");
            e.Property(x => x.Status).HasMaxLength(50).IsRequired()
                .HasConversion<string>();
            var inputsJson = e.Property(x => x.InputsJson).HasDefaultValue("{}");
            if (jsonType != null) inputsJson.HasColumnType(jsonType);
            e.HasIndex(x => x.RequesterId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CorrelationId);
            e.Property(x => x.ExternalTicketKey).HasColumnName("external_ticket_key").HasMaxLength(100);
            e.Property(x => x.ExternalTicketUrl).HasColumnName("external_ticket_url").HasMaxLength(500);
        });

        modelBuilder.Entity<FileAttachment>(e =>
        {
            e.ToTable("file_attachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.InputId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Filename).HasMaxLength(500).IsRequired();
            e.Property(x => x.BlobReference).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.HasOne(x => x.ServiceRequest)
                .WithMany(x => x.Attachments)
                .HasForeignKey(x => x.ServiceRequestId);
        });

        modelBuilder.Entity<ExecutionResult>(e =>
        {
            e.ToTable("execution_results");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            var outputJson = e.Property(x => x.OutputJson);
            if (jsonType != null) outputJson.HasColumnType(jsonType);
            e.HasOne(x => x.ServiceRequest)
                .WithMany(x => x.ExecutionResults)
                .HasForeignKey(x => x.ServiceRequestId);
        });

        // Approvals
        modelBuilder.Entity<ApprovalRequest>(e =>
        {
            e.ToTable("approval_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Strategy).HasMaxLength(20).IsRequired()
                .HasConversion<string>();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.EscalationGroup).HasMaxLength(200);
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.ServiceRequest)
                .WithOne(x => x.ApprovalRequest)
                .HasForeignKey<ApprovalRequest>(x => x.ServiceRequestId);
        });

        modelBuilder.Entity<ApprovalDecision>(e =>
        {
            e.ToTable("approval_decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ApproverId).HasMaxLength(100).IsRequired();
            e.Property(x => x.ApproverName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Decision).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.ApproverId);
            e.HasOne(x => x.ApprovalRequest)
                .WithMany(x => x.Decisions)
                .HasForeignKey(x => x.ApprovalRequestId);
        });

        // Audit Log
        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Module).HasMaxLength(50).IsRequired();
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ActorType).HasMaxLength(20).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(50).IsRequired();
            var beforeState = e.Property(x => x.BeforeState);
            var afterState = e.Property(x => x.AfterState);
            var auditMetadata = e.Property(x => x.Metadata);
            if (jsonType != null)
            {
                beforeState.HasColumnType(jsonType);
                afterState.HasColumnType(jsonType);
                auditMetadata.HasColumnType(jsonType);
            }
            e.Property(x => x.SourceIp).HasMaxLength(45);
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.ActorId);
            e.HasIndex(x => new { x.Module, x.Action });
        });

        // Deploy Events
        modelBuilder.Entity<DeployEvent>(e =>
        {
            e.ToTable("deploy_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Product).HasMaxLength(200).IsRequired();
            e.Property(x => x.Service).HasMaxLength(200).IsRequired();
            e.Property(x => x.Environment).HasMaxLength(100).IsRequired();
            e.Property(x => x.Version).HasMaxLength(200).IsRequired();
            e.Property(x => x.PreviousVersion).HasMaxLength(200);
            e.Property(x => x.IsRollback).HasDefaultValue(false);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("succeeded").IsRequired();
            e.Property(x => x.Source).HasMaxLength(50).IsRequired();
            var referencesJson = e.Property(x => x.ReferencesJson).HasDefaultValue("[]");
            var participantsJson = e.Property(x => x.ParticipantsJson).HasDefaultValue("[]");
            var enrichmentJson = e.Property(x => x.EnrichmentJson);
            var metadataJson = e.Property(x => x.MetadataJson).HasDefaultValue("{}");
            if (jsonType != null)
            {
                referencesJson.HasColumnType(jsonType);
                participantsJson.HasColumnType(jsonType);
                enrichmentJson.HasColumnType(jsonType);
                metadataJson.HasColumnType(jsonType);
            }
            e.HasIndex(x => new { x.Product, x.Service, x.Environment, x.DeployedAt })
                .IsDescending(false, false, false, true);
            e.HasIndex(x => x.Product);
            e.HasIndex(x => new { x.Environment, x.DeployedAt })
                .IsDescending(false, true);
            e.Ignore(x => x.References);
            e.Ignore(x => x.Participants);
            e.Ignore(x => x.Enrichment);
            e.Ignore(x => x.Metadata);
        });

        modelBuilder.Entity<DeployEventWorkItem>(e =>
        {
            e.ToTable("deploy_event_work_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkItemKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.Product).HasMaxLength(200).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(50);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Revision).HasMaxLength(200);
            e.HasOne<DeployEvent>()
                .WithMany()
                .HasForeignKey(x => x.DeployEventId)
                .OnDelete(DeleteBehavior.Cascade);
            // Unique per (event, key) so re-ingest of the same event is idempotent.
            e.HasIndex(x => new { x.DeployEventId, x.WorkItemKey }).IsUnique();
            // Lookup: "find all builds carrying ticket X in product Y".
            e.HasIndex(x => new { x.WorkItemKey, x.Product });
            // Lookup: "tickets in product Y" for inbox queries.
            e.HasIndex(x => x.Product);
        });

        // Webhook Subscriptions
        modelBuilder.Entity<WebhookSubscription>(e =>
        {
            e.ToTable("webhook_subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Url).HasMaxLength(2000).IsRequired();
            e.Property(x => x.EncryptedSecret).IsRequired();
            var eventsJson = e.Property(x => x.EventsJson).HasDefaultValue("[]");
            if (jsonType != null) eventsJson.HasColumnType(jsonType);
            e.Property(x => x.FilterProduct).HasMaxLength(200);
            e.Property(x => x.FilterEnvironment).HasMaxLength(100);
            e.HasIndex(x => x.Active);
        });

        // Webhook Deliveries
        modelBuilder.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            var payloadJson = e.Property(x => x.PayloadJson).HasDefaultValue("{}");
            if (jsonType != null) payloadJson.HasColumnType(jsonType);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.ResponseBody).HasMaxLength(4000);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasOne(x => x.Subscription)
                .WithMany(x => x.Deliveries)
                .HasForeignKey(x => x.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Status, x.NextRetryAt });
            e.HasIndex(x => x.SubscriptionId);
        });

        // Local Users (dev/test authentication)
        modelBuilder.Entity<LocalUser>(e =>
        {
            e.ToTable("local_users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(200).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            var rolesJson = e.Property(x => x.RolesJson).HasColumnName("Roles").HasDefaultValue("[]");
            if (jsonType != null) rolesJson.HasColumnType(jsonType);
            e.Ignore(x => x.Roles);
        });

        // Platform Settings — generic key/value store for feature flags, env topology, etc.
        modelBuilder.Entity<PlatformSetting>(e =>
        {
            e.ToTable("platform_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(4000).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(200).IsRequired();
        });

        // Promotion Policies
        modelBuilder.Entity<PromotionPolicy>(e =>
        {
            e.ToTable("promotion_policies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Product).HasMaxLength(200).IsRequired();
            e.Property(x => x.Service).HasMaxLength(200);
            e.Property(x => x.TargetEnv).HasMaxLength(100).IsRequired();
            e.Property(x => x.ApproverGroup).HasMaxLength(400);
            e.Property(x => x.Strategy).HasMaxLength(20).IsRequired().HasConversion<string>();
            e.Property(x => x.Gate).HasMaxLength(30).IsRequired().HasConversion<string>().HasDefaultValue(PromotionGate.PromotionOnly);
            e.Property(x => x.EscalationGroup).HasMaxLength(400);
            // Unique per (product, service?, target_env). SQL Server and Postgres both treat
            // NULL as distinct from NULL in unique indexes, which is the semantics we want:
            // one product-default row AND any number of service-specific rows.
            e.HasIndex(x => new { x.Product, x.Service, x.TargetEnv }).IsUnique();
            e.HasIndex(x => new { x.Product, x.TargetEnv });
        });

        // Promotion Candidates
        modelBuilder.Entity<PromotionCandidate>(e =>
        {
            e.ToTable("promotion_candidates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Product).HasMaxLength(200).IsRequired();
            e.Property(x => x.Service).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceEnv).HasMaxLength(100).IsRequired();
            e.Property(x => x.TargetEnv).HasMaxLength(100).IsRequired();
            e.Property(x => x.Version).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceDeployerName).HasMaxLength(300);
            e.Property(x => x.SourceDeployerEmail).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired().HasConversion<string>();
            e.Property(x => x.ExternalRunUrl).HasMaxLength(2000);
            var resolvedPolicyJson = e.Property(x => x.ResolvedPolicyJson);
            if (jsonType != null) resolvedPolicyJson.HasColumnType(jsonType);

            var participantsJson = e.Property(x => x.ParticipantsJson).HasDefaultValue("[]");
            if (jsonType != null) participantsJson.HasColumnType(jsonType);
            e.Ignore(x => x.Participants);

            var supersededIdsJson = e.Property(x => x.SupersededSourceEventIdsJson).HasDefaultValue("[]");
            if (jsonType != null) supersededIdsJson.HasColumnType(jsonType);
            e.Ignore(x => x.SupersededSourceEventIds);

            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.Product, x.Service, x.SourceEnv, x.TargetEnv });
            e.HasIndex(x => x.SourceDeployEventId);
        });

        // Promotion Approvals
        modelBuilder.Entity<PromotionApproval>(e =>
        {
            e.ToTable("promotion_approvals");
            e.HasKey(x => x.Id);
            e.Property(x => x.ApproverEmail).HasMaxLength(300).IsRequired();
            e.Property(x => x.ApproverName).HasMaxLength(300).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.Property(x => x.Decision).HasMaxLength(20).IsRequired().HasConversion<string>();
            // DB-level guard against double approval from the same user.
            e.HasIndex(x => new { x.CandidateId, x.ApproverEmail }).IsUnique();
            e.HasOne<PromotionCandidate>()
                .WithMany()
                .HasForeignKey(x => x.CandidateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemApproval>(e =>
        {
            e.ToTable("work_item_approvals");
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkItemKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.Product).HasMaxLength(200).IsRequired();
            e.Property(x => x.TargetEnv).HasMaxLength(100).IsRequired();
            e.Property(x => x.ApproverEmail).HasMaxLength(300).IsRequired();
            e.Property(x => x.ApproverName).HasMaxLength(300).IsRequired();
            e.Property(x => x.Decision).HasMaxLength(20).IsRequired().HasConversion<string>();
            e.Property(x => x.Comment).HasMaxLength(2000);
            // One decision per (ticket, product, env, approver). DB-level guard against double-decision.
            e.HasIndex(x => new { x.WorkItemKey, x.Product, x.TargetEnv, x.ApproverEmail }).IsUnique();
            // Lookup: "all decisions on FOO-123 for product X in env stage" — for the gate evaluator.
            e.HasIndex(x => new { x.WorkItemKey, x.Product, x.TargetEnv });
            // Lookup: "any decisions in this product+env" — admin queries.
            e.HasIndex(x => new { x.Product, x.TargetEnv });
        });

        // Promotion Comments
        modelBuilder.Entity<PromotionComment>(e =>
        {
            e.ToTable("promotion_comments");
            e.HasKey(x => x.Id);
            e.Property(x => x.AuthorEmail).HasMaxLength(300).IsRequired();
            e.Property(x => x.AuthorName).HasMaxLength(300).IsRequired();
            e.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            e.HasIndex(x => new { x.CandidateId, x.CreatedAt });
            e.HasOne<PromotionCandidate>()
                .WithMany()
                .HasForeignKey(x => x.CandidateId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
