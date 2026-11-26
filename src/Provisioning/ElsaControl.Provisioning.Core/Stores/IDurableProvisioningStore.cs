namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Marker for a replica-safe store on Control's own SQL database. In-memory
/// is tests-only. The API host refuses to start the worker without one.
/// </summary>
public interface IDurableProvisioningStore;
