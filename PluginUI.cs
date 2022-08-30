using System;
using System.IO;
using System.Numerics;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace ARealmRecorded;

public static class PluginUI
{
    public static readonly float[] presetSpeeds = { 0.5f, 1, 2, 5, 10, 20, 60 };

    private static int editingRecording = -1;
    private static string editingName = string.Empty;

    private static bool loadingPlayback = false;
    private static bool loadedPlayback = true;

    public static void Draw()
    {
        DrawExpandedDutyRecorderMenu();
        DrawExpandedPlaybackControls();
    }

    public static unsafe void DrawExpandedDutyRecorderMenu()
    {
        if (DalamudApi.GameGui.GameUiHidden) return;

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplaySetting", 1);
        if (addon == null || !addon->IsVisible || (addon->Flags & 16) == 0) return;

        var agent = DalamudApi.GameGui.FindAgentInterface((IntPtr)addon);
        if (agent == IntPtr.Zero) return;

        //var units = AtkStage.GetSingleton()->RaptureAtkUnitManager->AtkUnitManager.FocusedUnitsList;
        //var count = units.Count;
        //if (count > 0 && (&units.AtkUnitEntries)[count - 1] != addon) return;

        var addonW = addon->RootNode->GetWidth() * addon->Scale;
        var addonH = (addon->RootNode->GetHeight() - 11) * addon->Scale;
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(new(addon->X + addonW, addon->Y));
        ImGui.SetNextWindowSize(new Vector2(300 * ImGuiHelpers.GlobalScale, addonH));
        ImGui.Begin("Expanded Duty Recorder", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.SyncAlt.ToIconString()))
            Game.GetReplayList();
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.FolderOpen.ToIconString()))
            Game.OpenReplayFolder();
        ImGui.PopFont();
        ImGui.SameLine();
        if (ImGui.Checkbox("Enable Recording Icon", ref ARealmRecorded.Config.EnableRecordingIcon))
            ARealmRecorded.Config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enables the game's recording icon next to the world / time information (Server info bar).");

        ImGui.BeginChild("Recordings List", ImGui.GetContentRegionAvail(), true);
        for (int i = 0; i < Game.ReplayList.Count; i++)
        {
            var (file, header) = Game.ReplayList[i];
            var name = file.Name;

            if (editingRecording != i)
            {
                var isPlayable = header.IsPlayable;

                if (!isPlayable)
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));

                if (ImGui.Selectable($"{name}", name == Game.lastSelectedReplay && *(byte*)(agent + 0x2C) == 100))
                    Game.SetDutyRecorderMenuSelection(agent, name, header);

                if (!isPlayable)
                    ImGui.PopStyleColor();

                if (ImGui.BeginPopupContextItem())
                {
                    for (byte j = 0; j < 3; j++)
                    {
                        if (ImGui.Selectable($"Copy to slot #{j + 1}"))
                            Game.CopyRecordingIntoSlot(agent, file, header, j);
                    }
                    ImGui.EndPopup();
                }

                if (!ImGui.IsItemHovered() || !ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) continue;

                editingRecording = i;
                editingName = name[..name.LastIndexOf('.')];
            }
            else
            {
                ImGui.InputText("##SetName", ref editingName, 64, ImGuiInputTextFlags.AutoSelectAll);

                if (ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive())
                    ImGui.SetKeyboardFocusHere(-1);

                if (!ImGui.IsItemDeactivated()) continue;

                editingRecording = -1;

                try
                {
                    file.MoveTo(Path.Combine(file.DirectoryName!, $"{editingName}.dat"));
                }
                catch (Exception e)
                {
                    ARealmRecorded.PrintError($"Failed to rename recording\n{e}");
                }
            }
        }
        ImGui.EndChild();
    }

    public static unsafe void DrawExpandedPlaybackControls()
    {
        if (DalamudApi.GameGui.GameUiHidden) return;
        //if (Game.ffxivReplay->selectedChapter != 64) return; // Apparently people don't like this :(

        if (!Game.InPlayback)
        {
            loadingPlayback = false;
            loadedPlayback = false;
            return;
        }

        if (!loadedPlayback)
        {
            if (Game.ffxivReplay->u0x6F8 != 0)
                loadingPlayback = true;
            else if (loadingPlayback && Game.ffxivReplay->u0x6F8 == 0)
                loadedPlayback = true;
            return;
        }

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplayPlayer", 1);
        if (addon == null || !addon->IsVisible) return;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.Begin("Expanded Playback", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
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