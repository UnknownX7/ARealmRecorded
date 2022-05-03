using System;
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

        //Config = (Configuration)DalamudApi.PluginInterface.GetPluginConfig() ?? new();
        //Config.Initialize();

        try
        {
            Game.Initialize();

            //DalamudApi.Framework.Update += Update;
            DalamudApi.PluginInterface.UiBuilder.Draw += Draw;
            //DalamudApi.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed loading ARealmRecorded\n{e}");
        }
    }

    //public void ToggleConfig() => PluginUI.isVisible ^= true;

    [Command("/addchapter")]
    [HelpMessage("Adds a chapter to the current recording.")]
    private void OnAddChapter(string command, string argument)
    {
        if (Game.AddRecordingChapter(2))
            PrintEcho("Chapter added!");
    }

    public static void PrintEcho(string message) => DalamudApi.ChatGui.Print($"[A Realm Recorded] {message}");
    public static void PrintError(string message) => DalamudApi.ChatGui.PrintError($"[A Realm Recorded] {message}");

    //private void Update(Framework framework) { }

    private void Draw() => PluginUI.Draw();

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        //Config.Save();

        //DalamudApi.Framework.Update -= Update;
        DalamudApi.PluginInterface.UiBuilder.Draw -= Draw;
        //DalamudApi.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
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