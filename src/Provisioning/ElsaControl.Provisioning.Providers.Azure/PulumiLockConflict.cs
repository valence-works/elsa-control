using Pulumi.Automation.Commands.Exceptions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Self-managed Pulumi backend lock messages. Matches ConcurrentUpdate and
/// the DIY backend's currently-locked / lock-conflict / [409] text.
/// </summary>
public static class PulumiLockConflict
{
    public static bool Matches(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConcurrentUpdateException)
                return true;
            var name = current.GetType().Name;
            if (name.Contains("ConcurrentUpdate", StringComparison.OrdinalIgnoreCase))
                return true;
            var message = current.Message;
            if (message.Contains("currently locked", StringComparison.OrdinalIgnoreCase)
                || message.Contains("lock conflict", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[409]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
