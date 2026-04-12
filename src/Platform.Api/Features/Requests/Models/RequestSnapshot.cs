namespace Platform.Api.Features.Requests.Models;

// RequestSnapshot is represented by CatalogItemVersion — the snapshot_id FK on ServiceRequest
// points to the CatalogItemVersion that was current when the request was submitted.
// This ensures the request is evaluated against the exact YAML definition at submission time.
