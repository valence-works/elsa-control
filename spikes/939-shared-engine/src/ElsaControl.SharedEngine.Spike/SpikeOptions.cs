namespace ElsaControl.SharedEngine.Spike;

public sealed class SpikeOptions
{
    public string Mode { get; set; } = "shard";
    public string DataDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "App_Data");
    public string? SqlConnection { get; set; }
    public int ProbeShellCount { get; set; } = 64;
    public string ProbeHostPrefix { get; set; } = "probe";
}
