namespace Platform.Api.Features.Requests.Models;

public enum RequestStatus
{
    Draft,
    Validating,
    ValidationFailed,
    AwaitingApproval,
    Executing,
    Completed,
    Failed,
    Retrying,
    Rejected,
    ChangesRequested,
    TimedOut,
    ManuallyResolved,
    Cancelled
}
