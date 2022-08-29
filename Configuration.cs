using Dalamud.Configuration;

namespace ARealmRecorded;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }
    public string LastLoadedReplay;
    public bool EnableRecordingIcon = false;

    public void Initialize() { }

    public void Save() => DalamudApi.PluginInterface.SavePluginConfig(this);
}