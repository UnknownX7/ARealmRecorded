using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ARealmRecorded;

public class ARealmRecorded : DalamudPlugin<Configuration>, IDalamudPlugin
{
    public string Name => "A Realm Recorded";

    public ARealmRecorded(DalamudPluginInterface pluginInterface) : base(pluginInterface) { }

    protected override void Initialize()
    {
        Game.Initialize();
        DalamudApi.ToastGui.Toast += OnToast;
    }

    protected override unsafe void ToggleConfig() => AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsReplaySetting)->Show();

    protected override void Draw() => PluginUI.Draw();

    private static unsafe void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        if (isHandled || !Common.ContentsReplayModule->IsLoadingChapter && Common.ContentsReplayModule->speed < 5) return;
        isHandled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        DalamudApi.ToastGui.Toast -= OnToast;
        Game.Dispose();
    }
}