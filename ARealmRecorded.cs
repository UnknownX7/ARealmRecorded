using System;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Logging;
using Dalamud.Plugin;

namespace ARealmRecorded;

public class ARealmRecorded : IDalamudPlugin
{
    public string Name => "A Realm Recorded";
    public static ARealmRecorded Plugin { get; private set; }
    public static Configuration Config { get; private set; }

    public ARealmRecorded(DalamudPluginInterface pluginInterface)
    {
        Plugin = this;
        DalamudApi.Initialize(this, pluginInterface);

        Config = (Configuration)DalamudApi.PluginInterface.GetPluginConfig() ?? new();
        Config.Initialize();

        try
        {
            Game.Initialize();

            //DalamudApi.Framework.Update += Update;
            DalamudApi.PluginInterface.UiBuilder.Draw += Draw;
            //DalamudApi.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
            DalamudApi.ToastGui.Toast += OnToast;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed loading ARealmRecorded\n{e}");
        }
    }

    //public void ToggleConfig() => PluginUI.isVisible ^= true;

    public static void PrintEcho(string message) => DalamudApi.ChatGui.Print($"[A Realm Recorded] {message}");
    public static void PrintError(string message) => DalamudApi.ChatGui.PrintError($"[A Realm Recorded] {message}");

    //private void Update(Framework framework) { }

    private void Draw() => PluginUI.Draw();

    private unsafe void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        if (isHandled || !Game.IsLoadingChapter && Game.ffxivReplay->speed < 5) return;
        isHandled = true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        Config.Save();

        //DalamudApi.Framework.Update -= Update;
        DalamudApi.PluginInterface.UiBuilder.Draw -= Draw;
        //DalamudApi.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        DalamudApi.ToastGui.Toast -= OnToast;
        DalamudApi.Dispose();

        Game.Dispose();
        Memory.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}