namespace ElsaControl.Deployment.Abstractions.Instances;

/// <summary>
/// Customer-safe classification of a provider failure. Raw ARM or Pulumi codes
/// never appear in this enum. Unclassified is the default for rows that have
/// not been inspected, including crash-loop and health-timeout outcomes.
/// </summary>
public enum ElsaInstanceOperationFailureClass
{
    Unclassified = 0,
    Transient = 1,
    Terminal = 2
}
