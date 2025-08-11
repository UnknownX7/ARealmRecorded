using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hypostasis.Game.Structures;
using Dalamud.Bindings.ImGui;

namespace ARealmRecorded;

public static unsafe class PlaybackControlsUI
{
    public static readonly float[] presetSpeeds = [ 0.5f, 1, 2, 5, 10, 20 ];

    private static bool loadingPlayback = false;
    private static bool loadedPlayback = true;

    private static bool shouldPlaybackControlHide = false;
    private static bool showReplaySettings = false;
    private static bool showDebug = false;

    private static float lastSeek = 0;
    private static bool showUnstuckButton = false;
    private static readonly Stopwatch unstuckTimer = new();

    public static void Draw()
    {
        if (DalamudApi.GameGui.GameUiHidden || DalamudApi.Condition[ConditionFlag.WatchingCutscene]) return;

        if (!Common.ContentsReplayModule->InPlayback)
        {
            loadingPlayback = false;
            loadedPlayback = false;
            return;
        }

        if (DalamudApi.GameGui.GetAddonByName("TalkSubtitle") != nint.Zero) return; // Hide during cutscenes

        if (Common.ContentsReplayModule->seek != lastSeek || Common.ContentsReplayModule->IsPaused)
        {
            lastSeek = Common.ContentsReplayModule->seek;
            unstuckTimer.Restart();
            showUnstuckButton = false;
        }
        else if (unstuckTimer.ElapsedMilliseconds > 3_000)
        {
            showUnstuckButton = true;
            loadedPlayback = true;
        }

        if (!loadedPlayback)
        {
            if (Common.ContentsReplayModule->u0x708 != 0)
            {
                loadingPlayback = true;
            }
            else if (loadingPlayback && Common.ContentsReplayModule->u0x708 == 0)
            {
                loadedPlayback = true;
                if (!ARealmRecorded.Config.EnableWaymarks)
                    Game.ToggleWaymarks();
            }
            return;
        }

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplayPlayer").Address;
        if (addon == null || (ARealmRecorded.Config.EnablePlaybackControlHiding && !addon->IsVisible && !showUnstuckButton))
        {
            shouldPlaybackControlHide = true;
            return;
        }

        using var _ = ImGuiEx.StyleVarBlock.Begin(ImGuiStyleVar.Alpha, ARealmRecorded.Config.EnablePlaybackControlHiding && shouldPlaybackControlHide && !showUnstuckButton ? 0.001f : 1);
        var addonW = addon->RootNode->GetWidth() * addon->Scale;
        var addonPadding = addon->Scale * 8;
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(new Vector2(addon->X + addonPadding, addon->Y + addonPadding) + ImGuiHelpers.MainViewport.Pos, ImGuiCond.Always, Vector2.UnitY);
        ImGui.SetNextWindowSize(new Vector2(addonW - addonPadding * 2, 0));
        ImGui.Begin("Expanded Playback", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);

        if (showReplaySettings && !Common.ContentsReplayModule->IsLoadingChapter)
        {
            DrawReplaySettings();
            ImGui.Separator();
        }
        else if (showDebug)
        {
            DrawDebug();
            ImGui.Separator();
        }

        //if (ImGuiEx.FontButton(FontAwesomeIcon.List.ToIconString(), UiBuilder.IconFont))
        //    ReplayListUI.DisplayDetachedReplayList ^= true;
        //ImGuiEx.SetItemTooltip("Display replay list.");

        //ImGui.SameLine();
        if (ImGuiEx.FontButton(FontAwesomeIcon.Users.ToIconString(), UiBuilder.IconFont))
            Framework.Instance()->GetUIModule()->EnterGPose();
        ImGuiEx.SetItemTooltip("Enters group pose.");

        ImGui.SameLine();
        if (ImGuiEx.FontButton(FontAwesomeIcon.Video.ToIconString(), UiBuilder.IconFont))
            Framework.Instance()->GetUIModule()->EnterIdleCam(0, DalamudApi.TargetManager.FocusTarget is { } focus ? focus.GameObjectId : 0xE0000000);
        ImGuiEx.SetItemTooltip("Enters idle camera on the current focus target.");

        ImGui.SameLine();
        var v = Game.IsWaymarkVisible;
        if (ImGuiEx.FontButton(v ? FontAwesomeIcon.ToggleOn.ToIconString() : FontAwesomeIcon.ToggleOff.ToIconString(), UiBuilder.IconFont))
        {
            Game.ToggleWaymarks();
            ARealmRecorded.Config.EnableWaymarks ^= true;
            ARealmRecorded.Config.Save();
        }
        ImGuiEx.SetItemTooltip(v ? "Hide waymarks." : "Show waymarks.");

        using (ImGuiEx.FontBlock.Begin(UiBuilder.IconFont))
        {
            ImGui.SameLine();
            if (ImGui.Button(FontAwesomeIcon.Cog.ToIconString()))
                showReplaySettings ^= true;

            ImGui.SameLine();
            ImGui.Button(FontAwesomeIcon.Skull.ToIconString());
            if (ImGui.BeginPopupContextItem(ImU8String.Empty, ImGuiPopupFlags.MouseButtonLeft))
            {
                if (ImGui.Selectable(FontAwesomeIcon.DoorOpen.ToIconString()))
                    Common.ContentsReplayModule->overallDataOffset = long.MaxValue;
                ImGui.EndPopup();
            }

#if DEBUG
            ImGui.SameLine();
            if (ImGui.Button(FontAwesomeIcon.ExclamationTriangle.ToIconString()))
                showDebug ^= true;
#endif
        }

        if (showUnstuckButton)
        {
            ImGui.SameLine();
            var segment = Game.GetReplayDataSegmentDetour(Common.ContentsReplayModule);

            using (ImGuiEx.StyleColorBlock.Begin(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TabActive)))
            {
                if (ImGui.Button("UNSTUCK") && segment != null)
                    Common.ContentsReplayModule->overallDataOffset += segment->Length;
            }
        }


        using (ImGuiEx.FontBlock.Begin(UiBuilder.IconFont))
        {
            var buttonSize = ImGui.CalcTextSize(FontAwesomeIcon.EyeSlash.ToIconString()) + ImGui.GetStyle().FramePadding * 2;
            ImGui.SameLine(ImGui.GetContentRegionMax().X - buttonSize.X);
            if (ImGui.Button(ARealmRecorded.Config.EnablePlaybackControlHiding ? FontAwesomeIcon.EyeSlash.ToIconString() : FontAwesomeIcon.Eye.ToIconString(), buttonSize))
            {
                ARealmRecorded.Config.EnablePlaybackControlHiding ^= true;
                ARealmRecorded.Config.Save();
            }
        }
        ImGuiEx.SetItemTooltip("Hides the menu under certain circumstances.");

        const int restartDelayMS = 12_000;
        var sliderWidth = ImGui.GetContentRegionAvail().X;
        var seekMS = Math.Max(Common.ContentsReplayModule->seek.ToMilliseconds(), (int)Common.ContentsReplayModule->chapters[0]->ms);
        var lastStartChapterMS = Common.ContentsReplayModule->chapters[Common.ContentsReplayModule->FindPreviousChapterType(2)]->ms;
        var nextStartChapterMS = Common.ContentsReplayModule->chapters[Common.ContentsReplayModule->FindNextChapterType(2)]->ms;
        if (lastStartChapterMS >= nextStartChapterMS)
            nextStartChapterMS = Common.ContentsReplayModule->replayHeader.totalMS;
        var currentTime = new TimeSpan(0, 0, 0, 0, (int)(seekMS - lastStartChapterMS));

        using (ImGuiEx.ItemWidthBlock.Begin(sliderWidth))
        {
            using (ImGuiEx.DisabledBlock.Begin(Common.ContentsReplayModule->IsLoadingChapter))
            using (ImGuiEx.StyleVarBlock.Begin(ImGuiStyleVar.GrabMinSize, 4))
                ImGui.SliderInt($"##Time{lastStartChapterMS}", ref seekMS, (int)lastStartChapterMS, (int)nextStartChapterMS - restartDelayMS, currentTime.ToString("hh':'mm':'ss"), ImGuiSliderFlags.NoInput);

            if (ImGui.IsItemHovered())
            {
                var hoveredWidth = ImGui.GetMousePos().X - ImGui.GetItemRectMin().X;
                var hoveredPercent = hoveredWidth / sliderWidth;
                if (hoveredPercent is >= 0.0f and <= 1.0f)
                {
                    var hoveredTime = new TimeSpan(0, 0, 0, 0, (int)Math.Min(Math.Max((int)((nextStartChapterMS - lastStartChapterMS - restartDelayMS) * hoveredPercent), 0), nextStartChapterMS - lastStartChapterMS));
                    ImGui.SetTooltip(hoveredTime.ToString("hh':'mm':'ss"));
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        ReplayManager.SeekToTime((uint)hoveredTime.TotalMilliseconds + lastStartChapterMS);
                    else if (ARealmRecorded.Config.EnableJumpToTime && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                        ReplayManager.JumpToTime((uint)hoveredTime.TotalMilliseconds + lastStartChapterMS);
                }
            }

            var speed = Common.ContentsReplayModule->speed;
            if (ImGui.SliderFloat("##Speed", ref speed, 0.05f, 10.0f, "%.2fx", ImGuiSliderFlags.AlwaysClamp))
                Common.ContentsReplayModule->speed = speed;
        }

        for (int i = 0; i < presetSpeeds.Length; i++)
        {
            if (i != 0)
                ImGui.SameLine();

            var s = presetSpeeds[i];
            if (ImGui.Button($"{s}x"))
                Common.ContentsReplayModule->speed = s == Common.ContentsReplayModule->speed ? 1 : s;
        }

        var customSpeed = ARealmRecorded.Config.CustomSpeedPreset;
        ImGui.SameLine();
        ImGui.Dummy(Vector2.Zero);
        ImGui.SameLine();
        if (ImGui.Button($"{customSpeed}x"))
            Common.ContentsReplayModule->speed = customSpeed == Common.ContentsReplayModule->speed ? 1 : customSpeed;

        shouldPlaybackControlHide = !ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.RectOnly);

        ImGui.End();
    }

    private static void DrawReplaySettings()
    {
        var save = false;

        if (ImGui.Checkbox("Hide Own Name (Requires Replay Restart)", ref ARealmRecorded.Config.EnableHideOwnName))
        {
            Game.replaceLocalPlayerNamePatch.Toggle();
            save = true;
        }

        save |= ImGui.Checkbox("Enable Quick Chapter Load", ref ARealmRecorded.Config.EnableQuickLoad);

        save |= ImGui.Checkbox("Enable Right Click to Jump to Time", ref ARealmRecorded.Config.EnableJumpToTime);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Doing this WILL result in playback not appearing correctly!");
        ImGui.SameLine();
        using (ImGuiEx.FontBlock.Begin(UiBuilder.IconFont))
            ImGui.TextColored(new Vector4(1, 1, 0, 1), FontAwesomeIcon.ExclamationTriangle.ToIconString());

        save |= ImGui.SliderFloat("Loading Speed", ref ARealmRecorded.Config.MaxSeekDelta, 100, 2000, "%.f%%");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Can cause issues with some fights that contain arena changes.");

        save |= ImGui.SliderFloat("Speed Preset", ref ARealmRecorded.Config.CustomSpeedPreset, 0.05f, 60, "%.2fx", ImGuiSliderFlags.AlwaysClamp);

        if (save)
            ARealmRecorded.Config.Save();
    }

    [Conditional("DEBUG")]
    private static void DrawDebug()
    {
        var segment = Game.GetReplayDataSegmentDetour(Common.ContentsReplayModule);
        if (segment == null) return;

        ImGui.TextUnformatted($"Offset: {Common.ContentsReplayModule->overallDataOffset + sizeof(FFXIVReplay.Header) + sizeof(FFXIVReplay.ChapterArray):X}");
        ImGui.TextUnformatted($"Opcode: {segment->opcode:X}");
        ImGui.TextUnformatted($"Data Length: {segment->dataLength}");
        ImGui.TextUnformatted($"Time: {segment->ms / 1000f}");
        ImGui.TextUnformatted($"Object ID: {segment->objectID:X}");
    }
}