namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Decides the durable backend owner for a first admitted Create. Later
/// operations must not call this; they read the stored owner.
/// </summary>
public interface IElsaInstanceProvisioningBackendSelector
{
    ElsaInstanceProvisioningBackend SelectForCreate();
}
