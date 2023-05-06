using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;

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

    protected override void Draw() => PluginUI.Draw();

    private static unsafe void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        if (isHandled || !Common.FFXIVReplay->IsLoadingChapter && Common.FFXIVReplay->speed < 5) return;
        isHandled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        DalamudApi.ToastGui.Toast -= OnToast;
        Game.Dispose();
    }
}