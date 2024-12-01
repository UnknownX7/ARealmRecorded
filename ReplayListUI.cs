using System;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.Config;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace ARealmRecorded;

public static unsafe class ReplayListUI
{
    private static bool displayDetachedReplayList = false;
    public static bool DisplayDetachedReplayList
    {
        get => displayDetachedReplayList;
        set => displayDetachedReplayList = value;
    }

    private static bool needSort = true;
    private static bool showPluginSettings = false;
    private static int editingReplay = -1;
    private static string editingName = string.Empty;
    private static readonly Regex displayNameRegex = new("(.+)[ _]\\d{4}\\.");

    public static void Draw()
    {
        var agent = nint.Zero;
        if (!displayDetachedReplayList)
        {
            if (DalamudApi.GameGui.GameUiHidden) return;

            var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplaySetting");
            if (addon == null || !addon->IsVisible || (addon->Flags198 & 512) == 0) return;

            agent = DalamudApi.GameGui.FindAgentInterface((nint)addon);
            if (agent == nint.Zero) return;

            //var units = AtkStage.GetSingleton()->RaptureAtkUnitManager->AtkUnitManager.FocusedUnitsList;
            //var count = units.Count;
            //if (count > 0 && (&units.AtkUnitEntries)[count - 1] != addon) return;

            var addonW = addon->RootNode->GetWidth() * addon->Scale;
            var addonH = (addon->RootNode->GetHeight() - 11) * addon->Scale;
            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGui.SetNextWindowPos(new Vector2(addon->X + addonW, addon->Y) + ImGuiHelpers.MainViewport.Pos);
            ImGui.SetNextWindowSize(new Vector2(500 * ImGuiHelpers.GlobalScale, addonH));
            ImGui.Begin("##ExpandedContentsReplaySetting", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        }
        else
        {
            if (!Common.ContentsReplayModule->InPlayback)
            {
                displayDetachedReplayList = false;
                return;
            }

            ImGui.SetNextWindowSize(ImGuiHelpers.ScaledVector2(500, 350));
            ImGui.Begin("Replay List", ref displayDetachedReplayList, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize);
        }

        ImGuiEx.AddDonationHeader();

        if (ImGui.IsWindowAppearing())
            showPluginSettings = false;

        using (ImGuiEx.FontBlock.Begin(UiBuilder.IconFont))
        {
            if (ImGui.Button(FontAwesomeIcon.SyncAlt.ToIconString()))
            {
                Game.GetReplayList();
                needSort = true;
            }

            ImGui.SameLine();
            if (ImGui.Button(FontAwesomeIcon.FolderOpen.ToIconString()))
                Game.OpenReplayFolder();

            ImGui.SameLine();
            if (ImGui.Button(FontAwesomeIcon.FileArchive.ToIconString()))
            {
                Game.ArchiveReplays();
                needSort = true;
            }
        }
        ImGuiEx.SetItemTooltip("Archive saved unplayable replays.");

        using (ImGuiEx.FontBlock.Begin(UiBuilder.IconFont))
        {
            ImGui.SameLine();
            if (ImGui.Button(FontAwesomeIcon.Cog.ToIconString()))
                showPluginSettings ^= true;
#if DEBUG
            ImGui.SameLine();
            if (ImGui.Button(FontAwesomeIcon.ExclamationTriangle.ToIconString()))
                Game.ReadPackets(Game.LastSelectedReplay);
#endif
        }

        if (!displayDetachedReplayList)
        {
            using (ImGuiEx.StyleColorBlock.Begin(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TabActive)))
            {
                if (DalamudApi.GameConfig.UiConfig.TryGetBool(nameof(UiConfigOption.ContentsReplayEnable), out var b) && !b && ImGui.Button("RECORDING IS CURRENTLY DISABLED, CLICK HERE TO ENABLE"))
                    DalamudApi.GameConfig.UiConfig.Set(nameof(UiConfigOption.ContentsReplayEnable), true);
            }
        }
        else
        {
            // TODO: Look into why this doesn't work (and block it for unplayable replays)
            if (ImGui.Button("Load Replay"))
            {
                ReplayManager.LoadReplay(Game.LastSelectedReplay);
                Common.ContentsReplayModule->SetChapter(0);
            }
        }

        if (!showPluginSettings)
            DrawReplaysTable(agent);
        else
            DrawPluginSettings();

        ImGui.End();
    }

    public static void DrawReplaysTable(nint agent)
    {
        if (!ImGui.BeginTable("ReplaysTable", 2, ImGuiTableFlags.Sortable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var sortspecs = ImGui.TableGetSortSpecs();
        if (sortspecs.SpecsDirty || needSort || ImGui.IsWindowAppearing())
        {
            if (sortspecs.Specs.ColumnIndex == 0)
            {
                // Date
                Game.ReplayList = sortspecs.Specs.SortDirection == ImGuiSortDirection.Ascending
                    ? [ .. Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenBy(t => t.Item2.header.timestamp) ]
                    : [ .. Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenByDescending(t => t.Item2.header.timestamp) ];
            }
            else
            {
                // Name
                Game.ReplayList = sortspecs.Specs.SortDirection == ImGuiSortDirection.Ascending
                    ? [ .. Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenBy(t => t.Item1.Name) ]
                    : [ .. Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenByDescending(t => t.Item1.Name) ];
            }
            sortspecs.SpecsDirty = false;
            needSort = false;
        }

        for (int i = 0; i < Game.ReplayList.Count; i++)
        {
            var (file, replay) = Game.ReplayList[i];
            var header = replay.header;
            var path = file.FullName;
            var fileName = file.Name;
            var displayName = displayNameRegex.Match(fileName) is { Success: true } match ? match.Groups[1].Value : fileName[..fileName.LastIndexOf('.')];
            var isPlayable = replay.header.IsPlayable;
            var autoRenamed = file.Directory?.Name == "autorenamed";

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (ImGuiEx.StyleColorBlock.Begin(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), !isPlayable))
                ImGui.TextUnformatted(DateTimeOffset.FromUnixTimeSeconds(header.timestamp).LocalDateTime.ToString("g"));
            ImGui.TableNextColumn();

            if (editingReplay != i)
            {
                using (ImGuiEx.StyleColorBlock.Begin(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), !isPlayable))
                {
                    if (ImGui.Selectable(autoRenamed ? $"◯ {displayName}##{path}" : $"{displayName}##{path}", path == Game.LastSelectedReplay && (agent == nint.Zero || *(byte*)(agent + 0x2C) == 100), ImGuiSelectableFlags.SpanAllColumns))
                    {
                        if (agent != nint.Zero)
                            Game.SetDutyRecorderMenuSelection(agent, path, header);
                        else
                            Game.LastSelectedReplay = path;
                    }
                }

                if (replay.header.IsCurrentFormatVersion && ImGui.IsItemHovered())
                {
                    var (pulls, longestPull) = replay.GetPullInfo();

                    ImGui.BeginTooltip();

                    using (ImGuiEx.StyleVarBlock.Begin(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
                    {
                        ImGui.TextUnformatted($"Duty: {header.ContentFinderCondition.Name.ToDalamudString()}");
                        if ((header.info & 4) != 0)
                        {
                            ImGui.SameLine();
                            ImGui.TextUnformatted(" ");
                            ImGui.SameLine();
                            using (ImGuiEx.FontBlock.Begin(UiBuilder.IconFont))
                                ImGui.TextColored(new Vector4(0, 1, 0, 1), FontAwesomeIcon.Check.ToIconString());
                        }

                        var foundPlayer = false;
                        ImGui.TextUnformatted("Party:");
                        foreach (var row in header.ClassJobs.OrderBy(row => row.UIPriority))
                        {
                            ImGui.SameLine();
                            if (!foundPlayer && row.RowId == header.LocalPlayerClassJob.RowId)
                            {
                                ImGui.TextUnformatted($" «{row.Abbreviation}»");
                                foundPlayer = true;
                            }
                            else
                            {
                                ImGui.TextUnformatted($" {row.Abbreviation}");
                            }
                        }

                        ImGui.TextUnformatted($"Length: {new TimeSpan(0, 0, 0, 0, (int)header.displayedMS):hh':'mm':'ss}");
                        if (pulls > 1)
                        {
                            ImGui.TextUnformatted($"Number of Pulls: {pulls}");
                            ImGui.TextUnformatted($"Longest Pull: {longestPull:hh':'mm':'ss}");
                        }
                    }

                    ImGui.EndTooltip();
                }

                if (ImGui.BeginPopupContextItem())
                {
                    if (agent != nint.Zero)
                    {
                        for (byte j = 0; j < 3; j++)
                        {
                            if (!ImGui.Selectable($"Copy to slot #{j + 1}")) continue;
                            Game.CopyReplayIntoSlot(agent, file, header, j);
                            needSort = true;
                        }
                    }

                    if (ImGui.Selectable("Delete"))
                    {
                        Game.DeleteReplay(file);
                        needSort = true;
                    }

                    ImGui.EndPopup();
                }

                if (!ImGui.IsItemHovered() || !ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) continue;

                editingReplay = i;
                editingName = fileName[..fileName.LastIndexOf('.')];
            }
            else
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputText("##SetName", ref editingName, 64, ImGuiInputTextFlags.AutoSelectAll);

                if (ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive())
                    ImGui.SetKeyboardFocusHere(-1);

                if (!ImGui.IsItemDeactivated()) continue;

                editingReplay = -1;

                if (ImGui.IsItemDeactivatedAfterEdit())
                    Game.RenameReplay(file, editingName);
            }
        }
        ImGui.EndTable();
    }

    public static void DrawPluginSettings()
    {
        ImGui.BeginChild("PluginSettings", Vector2.Zero, true);

        var save = false;

        save |= ImGui.Checkbox("Enable Recording Icon", ref ARealmRecorded.Config.EnableRecordingIcon);
        ImGuiEx.SetItemTooltip("Enables the game's recording icon next to the world / time information (Server info bar).");

        save |= ImGui.InputInt("Max Replays", ref ARealmRecorded.Config.MaxAutoRenamedReplays);
        ImGuiEx.SetItemTooltip("Max number of replays to keep in the autorenamed folder.");

        save |= ImGui.InputInt("Max Deleted Replays", ref ARealmRecorded.Config.MaxDeletedReplays);
        ImGuiEx.SetItemTooltip("Max number of replays to keep in the deleted folder.");

        if (save)
            ARealmRecorded.Config.Save();

        ImGui.EndChild();
    }
}