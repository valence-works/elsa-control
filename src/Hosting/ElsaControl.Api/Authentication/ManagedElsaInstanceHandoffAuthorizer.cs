using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Authentication;

public sealed class ManagedElsaInstanceHandoffAuthorizer(
    IManagedElsaInstanceIdentityStore identities,
    AccountWorkspaceService accounts,
    WorkspacePermissionService permissions) : IManagedElsaHandoffAuthorizer
{
    public async ValueTask<ManagedElsaHandoffAuthorization?> AuthorizeAsync(
        TrustedWorkspaceIdentity identity,
        ManagedElsaHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await identities.FindScopeAsync(request.OrganizationId, request.InstanceId, cancellationToken);
        if (scope is null)
            return null;

        var access = await accounts.GetWorkspaceAccessAsync(identity, scope.WorkspaceId, cancellationToken);
        if (access is null || access.OrganizationId != scope.OrganizationId)
            return null;

        var effective = await permissions.GetEffectivePermissionsAsync(scope.WorkspaceId, access.AccountId, cancellationToken);
        if (!effective.Has(ManagedElsaInstancePermissions.Open))
            return null;

        var target = await identities.FindOpenableAsync(request.OrganizationId, request.InstanceId, cancellationToken);
        if (target is null ||
            !string.Equals(target.Audience, request.Audience, StringComparison.Ordinal) ||
            !ManagedElsaHandoffIssuer.HasExactRedirectBinding(target.CallbackUri, request.RedirectUri))
            return null;

        return new ManagedElsaHandoffAuthorization(
            access.AccountId,
            target.OrganizationId,
            target.InstanceId,
            target.Audience,
            target.CallbackUri,
            request.CodeChallenge,
            new HashSet<string>([ManagedElsaHandoffDefaults.RuntimeSessionScope], StringComparer.Ordinal),
            target.BindingVersion,
            ManagedElsaRuntimePermissionMapping.For(access));
    }

    public async ValueTask<bool> IsStillAuthorizedAsync(
        ManagedElsaHandoffClaims claims,
        CancellationToken cancellationToken = default)
    {
        var target = await identities.FindOpenableAsync(claims.OrganizationId, claims.InstanceId, cancellationToken);
        if (target is null || target.BindingVersion != claims.BindingVersion ||
            !string.Equals(target.Audience, claims.Audience, StringComparison.Ordinal) ||
            !ManagedElsaHandoffIssuer.HasExactRedirectBinding(target.CallbackUri, claims.RedirectUri))
            return false;

        var access = await accounts.GetWorkspaceAccessAsync(
            claims.ToTrustedWorkspaceIdentity(), target.WorkspaceId, cancellationToken);
        if (access is null || access.AccountId != claims.AccountId || access.OrganizationId != target.OrganizationId)
            return false;

        var effective = await permissions.GetEffectivePermissionsAsync(target.WorkspaceId, access.AccountId, cancellationToken);
        var currentRuntimePermissions = ManagedElsaRuntimePermissionMapping.For(access);
        return effective.Has(ManagedElsaInstancePermissions.Open) &&
               claims.RuntimePermissions.All(currentRuntimePermissions.Contains);
    }
}
