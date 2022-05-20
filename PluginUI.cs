using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace ARealmRecorded;

public static class PluginUI
{
    public static readonly float[] presetSpeeds = { 0.5f, 1, 2, 5, 10, 20, 60 };

    public static unsafe void Draw()
    {
        if (DalamudApi.GameGui.GameUiHidden || Game.ffxivReplay->selectedChapter != 64 || (Game.ffxivReplay->status & 0x80) == 0) return;

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplayPlayer", 1);
        if (addon == null || !addon->IsVisible) return;

        ImGui.Begin("Expanded Replay Settings", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        ImGui.SetWindowPos(new(addon->X, addon->Y - ImGui.GetWindowHeight()));

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Users.ToIconString()))
            Game.EnterGroupPose();
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.Video.ToIconString()))
            Game.EnterIdleCamera();
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enters idle camera on the current focus target.");
        ImGui.SameLine();
        ImGui.Checkbox("Quick Chapter Load", ref Game.quickLoadEnabled);

        ImGui.SetNextItemWidth(200);
        var speed = Game.ffxivReplay->speed;
        if (ImGui.SliderFloat("Speed", ref speed, 0.1f, 10, "%.1f", ImGuiSliderFlags.NoInput))
            Game.ffxivReplay->speed = speed;

        //var buttonSize = new Vector2(ImGui.CalcTextSize("aaaaa").X, 0);
        for (int i = 0; i < presetSpeeds.Length; i++)
        {
            if (i != 0)
                ImGui.SameLine();

            var s = presetSpeeds[i];
            if (ImGui.Button($"{s}x"))
                Game.ffxivReplay->speed = s == Game.ffxivReplay->speed ? 1 : s;
        }

        ImGui.End();
    }
}