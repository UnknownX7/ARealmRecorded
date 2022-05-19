using Dalamud.Configuration;

namespace ARealmRecorded;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }
    public string LastLoadedReplay;

    public void Initialize() { }

    public void Save() => DalamudApi.PluginInterface.SavePluginConfig(this);
}