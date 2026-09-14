namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

internal sealed class SyncRunReconciliationEventEntity
{
    public Guid Id { get; set; }
    public int ReconciledCount { get; set; }
    public DateTimeOffset ProcessStartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
