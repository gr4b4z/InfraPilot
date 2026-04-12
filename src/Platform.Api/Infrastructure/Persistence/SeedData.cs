using Platform.Api.Features.Approvals.Models;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Audit;

namespace Platform.Api.Infrastructure.Persistence;

public static class SeedData
{
    public static async Task Seed(PlatformDbContext db, CatalogYamlLoader loader)
    {
        // 1. Sync catalog from YAML
        var definitions = loader.LoadAll();
        var catalogItems = new Dictionary<string, CatalogItem>();

        foreach (var def in definitions)
        {
            var existing = db.CatalogItems.FirstOrDefault(c => c.Slug == def.Id);
            if (existing is not null)
            {
                catalogItems[def.Id] = existing;
                continue;
            }

            var item = new CatalogItem
            {
                Id = Guid.NewGuid(),
                Slug = def.Id,
                Name = def.Name,
                Description = def.Description,
                Category = def.Category,
                Icon = def.Icon,
                CurrentYamlHash = def.YamlHash,
                IsActive = true,
            };

            db.CatalogItems.Add(item);
            db.CatalogItemVersions.Add(new CatalogItemVersion
            {
                Id = Guid.NewGuid(),
                CatalogItemId = item.Id,
                YamlContent = def.YamlContent,
                YamlHash = def.YamlHash,
            });

            catalogItems[def.Id] = item;
        }

        await db.SaveChangesAsync();

        // 2. Skip if requests already exist
        if (db.ServiceRequests.Any()) return;

        var now = DateTimeOffset.UtcNow;

        // 3. Seed service requests in various states
        var req1 = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = catalogItems["create-repo"].Id,
            RequesterId = "user-1",
            RequesterName = "Jan Kowalski",
            Status = RequestStatus.Completed,
            InputsJson = """{"repo_name":"SWO-PLT-payments-intg","platform":"azure-devops","project":"MPT","template":"dotnet-api","setup_pipeline":true,"description":"Payments integration service"}""",
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-2),
        };

        var req2 = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = catalogItems["create-namespace"].Id,
            RequesterId = "user-1",
            RequesterName = "Jan Kowalski",
            Status = RequestStatus.AwaitingApproval,
            InputsJson = """{"namespace_name":"swo-plt-analytics","cluster":"aks-prod-weu","environment":"production","cpu_limit":4,"memory_limit":8,"enable_network_policy":true}""",
            CreatedAt = now.AddHours(-6),
            UpdatedAt = now.AddHours(-6),
        };

        var req3 = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = catalogItems["request-dns-record"].Id,
            RequesterId = "user-2",
            RequesterName = "Anna Nowak",
            Status = RequestStatus.Completed,
            InputsJson = """{"record_type":"CNAME","hostname":"api.softwareone.com","value":"swo-api-gateway.azurefd.net","ttl":3600,"zone":"external"}""",
            CreatedAt = now.AddDays(-5),
            UpdatedAt = now.AddDays(-4),
        };

        var req4 = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = catalogItems["request-role-assignment"].Id,
            RequesterId = "user-3",
            RequesterName = "Piotr Wiśniewski",
            Status = RequestStatus.Rejected,
            InputsJson = """{"target_user":"user-3","role":"owner","scope":"/subscriptions/abc-123/resourceGroups/prod-rg","justification":"Need owner access for deployment","duration":"permanent"}""",
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-1),
        };

        var req5 = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = catalogItems["run-pipeline"].Id,
            RequesterId = "user-1",
            RequesterName = "Jan Kowalski",
            Status = RequestStatus.Failed,
            InputsJson = """{"platform":"azure-devops","pipeline":"deploy-api","branch":"main","parameters":[{"key":"ENV","value":"staging"}]}""",
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddHours(-1),
        };

        var req6 = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = catalogItems["create-repo"].Id,
            RequesterId = "user-2",
            RequesterName = "Anna Nowak",
            Status = RequestStatus.Draft,
            InputsJson = """{"repo_name":"SWO-PLT-notifications","platform":"github","template":"node-api"}""",
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-30),
        };

        db.ServiceRequests.AddRange(req1, req2, req3, req4, req5, req6);
        await db.SaveChangesAsync();

        // 4. Execution results for completed/failed requests
        db.ExecutionResults.AddRange(
            new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = req1.Id,
                Attempt = 1,
                Status = "Completed",
                OutputJson = """{"repoUrl":"https://dev.azure.com/SWO/MPT/_git/SWO-PLT-payments-intg","pipelineId":1042}""",
                StartedAt = now.AddDays(-2).AddMinutes(-5),
                CompletedAt = now.AddDays(-2),
            },
            new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = req3.Id,
                Attempt = 1,
                Status = "Completed",
                OutputJson = """{"recordId":"dns-rec-789","fqdn":"api.softwareone.com"}""",
                StartedAt = now.AddDays(-4).AddMinutes(-2),
                CompletedAt = now.AddDays(-4),
            },
            new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = req5.Id,
                Attempt = 1,
                Status = "Failed",
                ErrorMessage = "Pipeline 'deploy-api' returned exit code 1: test stage failed — 3 unit tests failing",
                StartedAt = now.AddHours(-1).AddMinutes(-10),
                CompletedAt = now.AddHours(-1),
            }
        );

        // 5. Approval for req2 (pending) and req4 (rejected)
        var approval2 = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = req2.Id,
            Strategy = ApprovalStrategy.Any,
            Status = "Pending",
            TimeoutAt = now.AddHours(42),
            EscalationGroup = "SWO-PLT-TeamLeads",
            CreatedAt = now.AddHours(-6),
        };

        var approval4 = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = req4.Id,
            Strategy = ApprovalStrategy.All,
            Status = "Rejected",
            CreatedAt = now.AddDays(-2),
        };

        db.ApprovalRequests.AddRange(approval2, approval4);
        await db.SaveChangesAsync();

        db.ApprovalDecisions.Add(new ApprovalDecision
        {
            Id = Guid.NewGuid(),
            ApprovalRequestId = approval4.Id,
            ApproverId = "user-4",
            ApproverName = "Marek Zieliński",
            Decision = "Rejected",
            Comment = "Owner role on production resource group requires CISO approval. Please use Contributor instead or escalate through the security team.",
            DecidedAt = now.AddDays(-1),
        });

        // 6. Audit log entries
        var auditEntries = new List<AuditEntry>
        {
            Entry(req1.CorrelationId, "requests", "request.draft", "user-1", "Jan Kowalski", "user", "ServiceRequest", req1.Id, now.AddDays(-3)),
            Entry(req1.CorrelationId, "requests", "request.validating", "user-1", "Jan Kowalski", "user", "ServiceRequest", req1.Id, now.AddDays(-3).AddMinutes(1)),
            Entry(req1.CorrelationId, "requests", "request.executing", "system", "System", "system", "ServiceRequest", req1.Id, now.AddDays(-2).AddMinutes(-5)),
            Entry(req1.CorrelationId, "requests", "request.completed", "system", "System", "executor", "ServiceRequest", req1.Id, now.AddDays(-2)),

            Entry(req2.CorrelationId, "requests", "request.draft", "user-1", "Jan Kowalski", "user", "ServiceRequest", req2.Id, now.AddHours(-6)),
            Entry(req2.CorrelationId, "requests", "request.validating", "user-1", "Jan Kowalski", "user", "ServiceRequest", req2.Id, now.AddHours(-6).AddMinutes(1)),
            Entry(req2.CorrelationId, "requests", "request.awaitingapproval", "system", "System", "system", "ServiceRequest", req2.Id, now.AddHours(-6).AddMinutes(2)),
            Entry(req2.CorrelationId, "approvals", "approval.created", "system", "System", "system", "ApprovalRequest", approval2.Id, now.AddHours(-6).AddMinutes(2)),

            Entry(req4.CorrelationId, "requests", "request.draft", "user-3", "Piotr Wiśniewski", "user", "ServiceRequest", req4.Id, now.AddDays(-2)),
            Entry(req4.CorrelationId, "requests", "request.awaitingapproval", "system", "System", "system", "ServiceRequest", req4.Id, now.AddDays(-2).AddMinutes(5)),
            Entry(req4.CorrelationId, "approvals", "approval.rejected", "user-4", "Marek Zieliński", "user", "ApprovalRequest", approval4.Id, now.AddDays(-1)),
            Entry(req4.CorrelationId, "requests", "request.rejected", "system", "System", "system", "ServiceRequest", req4.Id, now.AddDays(-1)),

            Entry(req5.CorrelationId, "requests", "request.executing", "system", "System", "system", "ServiceRequest", req5.Id, now.AddHours(-1).AddMinutes(-10)),
            Entry(req5.CorrelationId, "requests", "request.failed", "system", "System", "executor", "ServiceRequest", req5.Id, now.AddHours(-1)),
        };

        db.AuditLog.AddRange(auditEntries);
        await db.SaveChangesAsync();
    }

    private static AuditEntry Entry(Guid correlationId, string module, string action, string actorId, string actorName, string actorType, string entityType, Guid entityId, DateTimeOffset timestamp)
    {
        return new AuditEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = timestamp,
            CorrelationId = correlationId,
            Module = module,
            Action = action,
            ActorId = actorId,
            ActorName = actorName,
            ActorType = actorType,
            EntityType = entityType,
            EntityId = entityId,
        };
    }
}
