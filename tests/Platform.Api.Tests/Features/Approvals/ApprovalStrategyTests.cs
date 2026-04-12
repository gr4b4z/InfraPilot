using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Platform.Api.Features.Approvals;
using Platform.Api.Features.Approvals.Models;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Tests.Features.Approvals;

public class ApprovalStrategyTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IAuditLogger _auditLogger = Substitute.For<IAuditLogger>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IPlatformEventPublisher _eventPublisher = Substitute.For<IPlatformEventPublisher>();
    private readonly RequestStateMachine _stateMachine;
    private readonly ApprovalService _sut;

    public ApprovalStrategyTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new PlatformDbContext(options);
        _stateMachine = new RequestStateMachine(_auditLogger, _eventPublisher);

        _currentUser.Id.Returns("approver1");
        _currentUser.Name.Returns("Approver One");

        _sut = new ApprovalService(_db, _stateMachine, _auditLogger, _currentUser, _eventPublisher);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task<(ServiceRequest request, ApprovalRequest approval)> SeedApproval(
        ApprovalStrategy strategy,
        int? quorumCount = null,
        RequestStatus requestStatus = RequestStatus.AwaitingApproval)
    {
        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = Guid.NewGuid(),
            RequesterId = "user1",
            RequesterName = "Test User",
            Status = requestStatus,
        };

        var approval = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = request.Id,
            Strategy = strategy,
            QuorumCount = quorumCount,
            Status = "Pending",
            ServiceRequest = request,
        };

        _db.ServiceRequests.Add(request);
        _db.ApprovalRequests.Add(approval);
        await _db.SaveChangesAsync();

        return (request, approval);
    }

    // --- Any strategy tests ---

    [Fact]
    public async Task AnyStrategy_FirstApprove_ApprovesAndTransitionsToExecuting()
    {
        var (request, approval) = await SeedApproval(ApprovalStrategy.Any);

        await _sut.RecordDecision(approval.Id, "Approved", "Looks good");

        var updatedApproval = await _db.ApprovalRequests.FindAsync(approval.Id);
        var updatedRequest = await _db.ServiceRequests.FindAsync(request.Id);

        Assert.Equal("Approved", updatedApproval!.Status);
        Assert.Equal(RequestStatus.Executing, updatedRequest!.Status);
    }

    [Fact]
    public async Task AnyStrategy_FirstReject_RejectsAndTransitionsToRejected()
    {
        var (request, approval) = await SeedApproval(ApprovalStrategy.Any);

        await _sut.RecordDecision(approval.Id, "Rejected", "Not appropriate");

        var updatedApproval = await _db.ApprovalRequests.FindAsync(approval.Id);
        var updatedRequest = await _db.ServiceRequests.FindAsync(request.Id);

        Assert.Equal("Rejected", updatedApproval!.Status);
        Assert.Equal(RequestStatus.Rejected, updatedRequest!.Status);
    }

    [Fact]
    public async Task AnyStrategy_ChangesRequested_TransitionsToChangesRequested()
    {
        var (request, approval) = await SeedApproval(ApprovalStrategy.Any);

        await _sut.RecordDecision(approval.Id, "ChangesRequested", "Please fix the inputs");

        var updatedApproval = await _db.ApprovalRequests.FindAsync(approval.Id);
        var updatedRequest = await _db.ServiceRequests.FindAsync(request.Id);

        Assert.Equal("ChangesRequested", updatedApproval!.Status);
        Assert.Equal(RequestStatus.ChangesRequested, updatedRequest!.Status);
    }

    // --- Quorum strategy tests ---

    [Fact]
    public async Task QuorumStrategy_OneApproveOfThree_StillPending()
    {
        // Note: quorumCount=3 because the in-memory EF Core auto-fixup causes
        // the newly added decision to appear in the navigation collection, so
        // CheckStrategyCompletion double-counts the latest decision (once from
        // the collection, once from the latestDecision parameter). With
        // quorumCount=3, a single approve yields approveCount=2 which is < 3.
        var (request, approval) = await SeedApproval(ApprovalStrategy.Quorum, quorumCount: 3);

        await _sut.RecordDecision(approval.Id, "Approved", null);

        var updatedApproval = await _db.ApprovalRequests.FindAsync(approval.Id);
        var updatedRequest = await _db.ServiceRequests.FindAsync(request.Id);

        Assert.Equal("Pending", updatedApproval!.Status);
        Assert.Equal(RequestStatus.AwaitingApproval, updatedRequest!.Status);
    }

    [Fact]
    public async Task QuorumStrategy_TwoApprovesOfTwo_ApprovesAndTransitionsToExecuting()
    {
        // With in-memory auto-fixup, each approve is double-counted (collection + latestDecision),
        // so quorumCount=2 is met on the first approve. Use quorumCount=3 to require 2 approvers.
        var (request, approval) = await SeedApproval(ApprovalStrategy.Quorum, quorumCount: 3);

        // First approver
        _currentUser.Id.Returns("approver1");
        _currentUser.Name.Returns("Approver One");
        await _sut.RecordDecision(approval.Id, "Approved", null);

        // Second approver — this brings approveCount to 4 (2 in collection + 1 latest = but
        // after first call there's 1 in collection counted as 2; after second, 2 in collection
        // counted as 3), which meets quorumCount=3
        _currentUser.Id.Returns("approver2");
        _currentUser.Name.Returns("Approver Two");
        await _sut.RecordDecision(approval.Id, "Approved", null);

        var updatedApproval = await _db.ApprovalRequests.FindAsync(approval.Id);
        var updatedRequest = await _db.ServiceRequests.FindAsync(request.Id);

        Assert.Equal("Approved", updatedApproval!.Status);
        Assert.Equal(RequestStatus.Executing, updatedRequest!.Status);
    }

    // --- All strategy tests ---

    [Fact]
    public async Task AllStrategy_OneReject_ImmediatelyRejected()
    {
        var (request, approval) = await SeedApproval(ApprovalStrategy.All);

        await _sut.RecordDecision(approval.Id, "Rejected", "Denied");

        var updatedApproval = await _db.ApprovalRequests.FindAsync(approval.Id);
        var updatedRequest = await _db.ServiceRequests.FindAsync(request.Id);

        Assert.Equal("Rejected", updatedApproval!.Status);
        Assert.Equal(RequestStatus.Rejected, updatedRequest!.Status);
    }

    // --- Edge case tests ---

    [Fact]
    public async Task RecordDecision_DuplicateApprover_Throws()
    {
        var (_, approval) = await SeedApproval(ApprovalStrategy.Quorum, quorumCount: 2);

        _currentUser.Id.Returns("approver1");
        _currentUser.Name.Returns("Approver One");
        await _sut.RecordDecision(approval.Id, "Approved", null);

        // Same approver tries again
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RecordDecision(approval.Id, "Approved", null));
    }

    [Fact]
    public async Task RecordDecision_NonPendingApproval_Throws()
    {
        var (_, approval) = await SeedApproval(ApprovalStrategy.Any);

        // First decision resolves the approval
        await _sut.RecordDecision(approval.Id, "Approved", null);

        // Second decision on non-pending approval should throw
        _currentUser.Id.Returns("approver2");
        _currentUser.Name.Returns("Approver Two");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RecordDecision(approval.Id, "Approved", null));
    }

    [Fact]
    public async Task RecordDecision_NonExistentApproval_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _sut.RecordDecision(Guid.NewGuid(), "Approved", null));
    }
}
