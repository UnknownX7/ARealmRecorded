using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ARealmRecorded;

public static class IPC
{
    private static ICallGateProvider<string, bool> loadReplayFromFile;
  
    public static void Initialize()
    {
        loadReplayFromFile = DalamudApi.PluginInterface.GetIpcProvider<string, bool>($"{DalamudApi.PluginInterface.InternalName}.LoadReplayFromFile");
        loadReplayFromFile.RegisterFunc(LoadReplayFromFile);
    }
  
    public static void Dispose()
    {
        loadReplayFromFile.UnregisterFunc();
    }
  
    private static unsafe bool LoadReplayFromFile(string path)
    {
        AgentInterface* agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsReplaySetting);
        if (!agent->IsAddonShown()) return false;
        return ReplayListUI.SetReplay((nint)agent, path);
    }
}
