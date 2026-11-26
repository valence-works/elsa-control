namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Value-free failure and outcome codes. Never include secrets, prices, or
/// cloud identifiers.
/// </summary>
public static class ProvisioningFailureCodes
{
    public const string NotImplemented = "provider.not-implemented";
    public const string StackLocked = "provider.stack.locked";
    public const string NothingToCancel = "operation.nothing-to-cancel";
    public const string RequestMissing = "operation.request-missing";
    public const string UnknownCommand = "operation.unknown-command";
    public const string ProviderFailed = "provider.failed";
    public const string LockConflict = "stack.lock-conflict";
    public const string CapabilityMissing = "provider.capability-missing";
    public const string PlatformAbsent = "platform.absent";
    public const string Cancelled = "operation.cancelled";
    public const string ResourceGroupPresent = "azure.resource-group.present";
    public const string FakeProviderForbidden = "provider.fake-forbidden";
}
