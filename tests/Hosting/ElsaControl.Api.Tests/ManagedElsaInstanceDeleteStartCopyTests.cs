using ElsaControl.Api.Workspace;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaInstanceDeleteStartCopyTests
{
    private const string SignedDeleteStartFailure =
        "We couldn't start deleting this engine. Please try again. If it keeps failing, email hello@valence.works with this engine's name and we'll help during business hours.";

    [Fact]
    public void Delete_start_failure_copy_is_the_signed_hello_sentence()
    {
        Assert.Equal(SignedDeleteStartFailure, ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail);
        Assert.DoesNotContain("contact support", ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operator recovery", ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\u2019', ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail);
        Assert.Contains("couldn't", ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail, StringComparison.Ordinal);
        Assert.Contains("engine's", ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail, StringComparison.Ordinal);
        Assert.Contains("we'll", ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail, StringComparison.Ordinal);
        Assert.Equal(3, ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictDetail.Count(ch => ch == '\''));
    }
}
