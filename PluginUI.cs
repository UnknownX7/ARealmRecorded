using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace ARealmRecorded;

public static unsafe class PluginUI
{
    public static readonly float[] presetSpeeds = { 0.5f, 1, 2, 5, 10, 20, 60 };

    private static int editingRecording = -1;
    private static string editingName = string.Empty;

    private static bool loadingPlayback = false;
    private static bool loadedPlayback = true;

    private static bool showSettings = false;
    private static bool showDebug = false;

    private static uint savedMS = 0;

    private static float lastSeek = 0;
    private static readonly Stopwatch lastSeekChange = new();

    private static unsafe uint GetUIWidth()
    {
        RaptureAtkUnitManager* manager = AtkStage.GetSingleton()->RaptureAtkUnitManager;
        if (manager == null) return 200;

        AtkUnitBase* unit = manager->GetAddonByName("ContentsReplayPlayer");
        if (unit == null) return 200;

        return (uint)(unit->RootNode->Width * unit->RootNode->ScaleX);
    }

    public static void Draw()
    {
        DrawExpandedDutyRecorderMenu();
        DrawExpandedPlaybackControls();
    }

    public static void DrawExpandedDutyRecorderMenu()
    {
        if (DalamudApi.GameGui.GameUiHidden) return;

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplaySetting", 1);
        if (addon == null || !addon->IsVisible || (addon->Flags & 16) == 0) return;

        var agent = DalamudApi.GameGui.FindAgentInterface((nint)addon);
        if (agent == nint.Zero) return;

        //var units = AtkStage.GetSingleton()->RaptureAtkUnitManager->AtkUnitManager.FocusedUnitsList;
        //var count = units.Count;
        //if (count > 0 && (&units.AtkUnitEntries)[count - 1] != addon) return;

        var addonW = addon->RootNode->GetWidth() * addon->Scale;
        var addonH = (addon->RootNode->GetHeight() - 11) * addon->Scale;
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(new(addon->X + addonW, addon->Y));
        ImGui.SetNextWindowSize(new Vector2(400 * ImGuiHelpers.GlobalScale, addonH));
        ImGui.Begin("Expanded Duty Recorder", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.SyncAlt.ToIconString()))
            Game.GetReplayList();
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.FolderOpen.ToIconString()))
            Game.OpenReplayFolder();
#if DEBUG
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.ExclamationTriangle.ToIconString()))
            Game.ReadPackets(Game.lastSelectedReplay);
#endif
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

            if (editingRecording != i)
            {
                var name = file.Name;
                var path = file.FullName;
                var isPlayable = header.IsPlayable;

                if (!isPlayable)
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));

                if (ImGui.Selectable(file.Directory?.Name == "autorenamed" ? $"◯ {name}" : name, path == Game.lastSelectedReplay && *(byte*)(agent + 0x2C) == 100))
                    Game.SetDutyRecorderMenuSelection(agent, path, header);

                if (!isPlayable)
                    ImGui.PopStyleColor();

                if (ImGui.BeginPopupContextItem())
                {
                    for (byte j = 0; j < 3; j++)
                    {
                        if (ImGui.Selectable($"Copy to slot #{j + 1}"))
                            Game.CopyRecordingIntoSlot(agent, file, header, j);
                    }

                    if (ImGui.Selectable("Delete"))
                        Game.DeleteRecording(file);

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

                if (ImGui.IsItemDeactivatedAfterEdit())
                    Game.RenameRecording(file, editingName);
            }
        }
        ImGui.EndChild();
    }

    public static void DrawExpandedPlaybackControls()
    {
        if (DalamudApi.GameGui.GameUiHidden || DalamudApi.Condition[ConditionFlag.WatchingCutscene]) return;
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
            {
                loadingPlayback = true;
            }
            else if (loadingPlayback && Game.ffxivReplay->u0x6F8 == 0)
            {
                loadedPlayback = true;
                savedMS = 0;
            }
            return;
        }

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplayPlayer", 1);
        if (addon == null) return;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(new(addon->X, addon->Y), ImGuiCond.Always, Vector2.UnitY);
        ImGui.Begin("Expanded Playback", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);

        if (showSettings && !Game.IsLoadingChapter)
        {
            DrawSettings();
            ImGui.Separator();
        }
        else if (showDebug)
        {
            DrawDebug();
            ImGui.Separator();
        }

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Users.ToIconString()))
            Game.EnterGroupPose();
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enters group pose.");

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Video.ToIconString()))
            Game.EnterIdleCamera();
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enters idle camera on the current focus target.");
        
        ImGui.SameLine();
        var v = Game.IsWaymarkVisible;
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(v ? FontAwesomeIcon.ToggleOn.ToIconString() : FontAwesomeIcon.ToggleOff.ToIconString()))
            Game.ToggleWaymarks();
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(v ? "Hide waymarks." : "Show waymarks.");
        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.Wrench.ToIconString()))
            showSettings ^= true;

        ImGui.SameLine();
        ImGui.Button(FontAwesomeIcon.Skull.ToIconString());
        if (ImGui.BeginPopupContextItem(null, ImGuiPopupFlags.MouseButtonLeft))
        {
            if (ImGui.Selectable(FontAwesomeIcon.DoorOpen.ToIconString()))
                Game.ffxivReplay->overallDataOffset = long.MaxValue;
            ImGui.EndPopup();
        }

#if DEBUG
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.ExclamationTriangle.ToIconString()))
            showDebug ^= true;
#endif
        ImGui.PopFont();

        var seek = Game.ffxivReplay->seek;
        if (Game.IsLoadingChapter)
        {
            if (seek != lastSeek)
            {
                lastSeek = seek;
                lastSeekChange.Restart();
            }

            if (lastSeekChange.ElapsedMilliseconds >= 3000)
            {
                ImGui.SameLine();
                var segment = Game.GetReplayDataSegmentDetour(Game.ffxivReplay);
                if (ImGui.Button("Unstuck") && segment != null)
                    Game.ffxivReplay->overallDataOffset += segment->Length;
            }

        }

        var slider_width = GetUIWidth() - (2 * ImGui.CalcTextSize("Speed").X);
        ImGui.SetNextItemWidth(slider_width);
        var start_ms = (float)Game.ffxivReplay->startingMS / 1000.0f;
        var seek_min = Game.ffxivReplay->seek - start_ms;
        var seek_max = Game.ffxivReplay->replayHeader.ms;
        var hours = MathF.Floor(seek_min / 3600.0f).ToString().PadLeft(2, '0');
        var minutes = MathF.Floor((seek_min % 3600.0f) / 60.0f).ToString().PadLeft(2, '0');
        var seconds = MathF.Truncate(seek_min % 60.0f).ToString().PadLeft(2, '0');
        if (ImGui.SliderFloat("Time", ref seek_min, 0.0f, (float)seek_max / 1000.0f, $"{hours}:{minutes}:{seconds}", ImGuiSliderFlags.NoInput)) {
            var time = seek_min + start_ms;
            var time_ms = (uint)(time * 1000.0f);
            var seg = Game.FindNextDataSegment(time_ms, out var offset);
            if (seg != null) {
                Game.ffxivReplay->overallDataOffset = offset;
                Game.ffxivReplay->seek = time;
            }
        }

        if (ImGui.IsItemHovered()) {
            var mouse_pos = ImGui.GetMousePos().X;
            var slider_pos = ImGui.GetItemRectMin().X;
            var preview_ms = (mouse_pos - slider_pos) / slider_width * (seek_max / 1000.0f);
            if (preview_ms >= 0.0f && preview_ms <= (seek_max / 1000.0f))
            {
                var preview_time = preview_ms + start_ms;
                var preview_hours = MathF.Floor(preview_time / 3600.0f).ToString().PadLeft(2, '0');
                var preview_minutes = MathF.Floor((preview_time % 3600.0f) / 60.0f).ToString().PadLeft(2, '0');
                var preview_seconds = MathF.Truncate(preview_time % 60.0f).ToString().PadLeft(2, '0');
                ImGui.BeginTooltip();
                ImGui.Text($"{preview_hours}:{preview_minutes}:{preview_seconds}");
                ImGui.EndTooltip();
            }
        }

        ImGui.SetNextItemWidth(slider_width);
        var speed = Game.ffxivReplay->speed;
        if (ImGui.SliderFloat("Speed", ref speed, 0.05f, 10.0f, "%.2f", ImGuiSliderFlags.NoInput))
            Game.ffxivReplay->speed = speed;

        for (int i = 0; i < presetSpeeds.Length; i++)
        {
            if (i != 0)
                ImGui.SameLine();

            var s = presetSpeeds[i];
            if (ImGui.Button($"{s}x"))
                Game.ffxivReplay->speed = s == Game.ffxivReplay->speed ? 1 : s;
        }

        if (ImGui.Button("Save Time"))
            savedMS = (uint)(Game.ffxivReplay->seek * 1000 - 3500);

        if (savedMS > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Jump to Time"))
                Game.SeekToTime(savedMS);
        }

        ImGui.End();
    }

    private static void DrawSettings()
    {
        var save = false;

        save |= ImGui.Checkbox("Quick Chapter Load", ref ARealmRecorded.Config.EnableQuickLoad);

        if (ARealmRecorded.Config.EnableQuickLoad)
        {
            ImGui.SetNextItemWidth(200);
            save |= ImGui.SliderFloat("Loading Speed", ref ARealmRecorded.Config.MaxSeekDelta, 100, 800, "%.f%%");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Can cause issues with some fights that contain arena changes.");
        }

        if (save)
            ARealmRecorded.Config.Save();
    }

    private static void DrawDebug()
    {
        var segment = Game.GetReplayDataSegmentDetour(Game.ffxivReplay);
        if (segment == null) return;

        ImGui.TextUnformatted($"Offset: {Game.ffxivReplay->overallDataOffset + sizeof(Structures.FFXIVReplay.Header) + sizeof(Structures.FFXIVReplay.ChapterArray):X}");
        ImGui.TextUnformatted($"Op-code: {segment->opcode:X}");
        ImGui.TextUnformatted($"Data Length: {segment->dataLength}");
        ImGui.TextUnformatted($"Time: {segment->ms / 1000f}");
        ImGui.TextUnformatted($"Object ID: {segment->objectID:X}");
    }
}