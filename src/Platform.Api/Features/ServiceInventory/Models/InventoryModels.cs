namespace Platform.Api.Features.ServiceInventory.Models;

// Storage of the service inventory (docs/plans/service-inventory.md, "Storage"). The raw file of
// every accepted fetch is kept in inventory_snapshots; every other table is a projection of one
// snapshot, rebuilt from it at will. Pages read through InventoryFeed.CurrentSnapshotId, which
// moves in the same SaveChanges that writes the snapshot and its rows.

/// <summary>
/// One feed's state: the feed itself (<see cref="Period"/> <c>""</c>) or, for cost, one month
/// (<c>FeedName:YYYY-MM</c>). Keeps the pointer to the current snapshot and what the last attempt did.
/// </summary>
public class InventoryFeed
{
    /// <summary><c>{FeedName}</c> or <c>{FeedName}:{Period}</c>.</summary>
    public string FeedKey { get; set; } = "";
    public string FeedName { get; set; } = "";
    public string FeedType { get; set; } = "";
    /// <summary><c>""</c> for the feed itself; YYYY-MM for one cost month.</summary>
    public string Period { get; set; } = "";
    public Guid? CurrentSnapshotId { get; set; }
    /// <summary>Manifest digest, ETag, Last-Modified or body hash, as the source reported it.</summary>
    public string? LastSourceVersion { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastChangedAt { get; set; }
    /// <summary>Why the last attempt did not replace the copy. Redacted. Null after a success.</summary>
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    /// <summary>Projector version that built the current snapshot's rows.</summary>
    public int ProjectionVersion { get; set; }
    /// <summary>Cost month only: the current snapshot is a closed month (<c>complete: true</c>).</summary>
    public bool Complete { get; set; }

    public static string KeyFor(string feedName, string period) =>
        string.IsNullOrEmpty(period) ? feedName : $"{feedName}:{period}";
}

/// <summary>The raw file of one accepted fetch, gzip-compressed.</summary>
public class InventorySnapshot
{
    public Guid Id { get; set; }
    public string FeedName { get; set; } = "";
    public string FeedType { get; set; } = "";
    public string Period { get; set; } = "";
    public string SourceKind { get; set; } = "";
    /// <summary>Where it came from, without any query string or credential.</summary>
    public string SourceLocation { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    /// <summary><c>sha256:</c> + hex of the uncompressed body.</summary>
    public string ContentSha256 { get; set; } = "";
    public int SchemaVersion { get; set; }
    /// <summary>The catalog version the file states (its own, or the one it was evaluated against).</summary>
    public int? DocumentVersion { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public bool? Complete { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public int ContentLength { get; set; }
    public byte[] ContentGzip { get; set; } = [];
}

/// <summary>A catalog service in one catalog snapshot.</summary>
public class InventoryService
{
    public Guid SnapshotId { get; set; }
    /// <summary>The catalog id, lower case. Equal to the portal's service name.</summary>
    public string ServiceId { get; set; } = "";
    public string Product { get; set; } = "";
    public string Team { get; set; } = "";
    public string Lifecycle { get; set; } = "";
}

/// <summary>One month's totals in one currency, for all sources (<see cref="Source"/> <c>""</c>) or one.</summary>
public class InventoryCostMonth
{
    public Guid SnapshotId { get; set; }
    public string Source { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Period { get; set; } = "";
    public bool Complete { get; set; }
    public decimal Total { get; set; }
    public decimal Direct { get; set; }
    public decimal Shared { get; set; }
    public decimal SharedUnsplit { get; set; }
    public decimal Unallocated { get; set; }
    /// <summary>Split only for all sources; null on a per-source row.</summary>
    public decimal? Untagged { get; set; }
    public decimal? UnknownTag { get; set; }
}

/// <summary>One service's cost in one month and one currency. Zero rows are kept.</summary>
public class InventoryCostService
{
    public Guid SnapshotId { get; set; }
    public string ServiceId { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Period { get; set; } = "";
    public string Product { get; set; } = "";
    public string Team { get; set; } = "";
    public decimal Direct { get; set; }
    public decimal Shared { get; set; }
    public decimal Total { get; set; }
}

/// <summary>Unallocated cost of one scope (subscription, project) and group, in one currency.</summary>
public class InventoryCostUnallocated
{
    public Guid SnapshotId { get; set; }
    public int Ordinal { get; set; }
    public string Period { get; set; } = "";
    public string Source { get; set; } = "";
    public string Scope { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Amount { get; set; }
}
