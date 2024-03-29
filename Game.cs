using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Hypostasis.Game.Structures;

namespace ARealmRecorded;

[HypostasisInjection]
public static unsafe class Game
{
    public static readonly string replayFolder = Path.Combine(Framework.Instance()->UserPath, "replay");
    public static readonly string autoRenamedFolder = Path.Combine(replayFolder, "autorenamed");
    public static readonly string archiveZip = Path.Combine(replayFolder, "archive.zip");
    public static readonly string deletedFolder = Path.Combine(replayFolder, "deleted");
    private static readonly Regex bannedFileCharacters = new("[\\\\\\/:\\*\\?\"\\<\\>\\|\u0000-\u001F]");

    private static List<(FileInfo, FFXIVReplay)> replayList;
    public static List<(FileInfo, FFXIVReplay)> ReplayList
    {
        get => replayList ?? GetReplayList();
        set => replayList = value;
    }

    public static string LastSelectedReplay { get; set; }
    private static FFXIVReplay.Header lastSelectedHeader;

    private static bool wasRecording = false;

    private static readonly HashSet<uint> whitelistedContentTypes = new() { 1, 2, 3, 4, 5, 9, 28, 29, 30 }; // 22 Event, 26 Eureka, 27 Carnivale

    private static readonly AsmPatch alwaysRecordPatch = new("A8 04 75 27 A8 02 74 23 48 8B", new byte?[] { 0xEB, 0x21 }, true); // 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90
    private static readonly AsmPatch removeRecordReadyToastPatch = new("BA CB 07 00 00 48 8B CF E8", new byte?[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly AsmPatch seIsABunchOfClownsPatch = new("80 78 ?? 80 72 04", new byte?[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly AsmPatch instantFadeOutPatch = new("44 8D 47 0A 33 D2", new byte?[] { null, null, 0x07, 0x90 }, true); // lea r8d, [rdi+0A] -> lea r8d, [rdi]
    private static readonly AsmPatch instantFadeInPatch = new("44 8D 42 0A 41 FF 92 ?? ?? 00 00", new byte?[] { null, null, null, 0x01 }, true); // lea r8d, [rdx+0A] -> lea r8d, [rdx+01]
    public static readonly AsmPatch replaceLocalPlayerNamePatch = new("75 ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? F6 05", new byte?[] { 0x90, 0x90 }, ARealmRecorded.Config.EnableHideOwnName);

    [HypostasisSignatureInjection("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 0E", Static = true, Offset = 0x48)]
    private static byte* waymarkToggle; // Actually a uint, but only seems to use the first 2 bits

    public static bool IsWaymarkVisible => (*waymarkToggle & 2) == 0;

    [HypostasisSignatureInjection("?? ?? 00 00 01 75 74 85 FF 75 07 E8")]
    public static short contentDirectorOffset;

    [HypostasisSignatureInjection("48 89 5C 24 10 57 48 81 EC 70 04 00 00")]
    private static delegate* unmanaged<nint, void> displaySelectedDutyRecording;
    public static void DisplaySelectedDutyRecording(nint agent) => displaySelectedDutyRecording(agent);

    [HypostasisSignatureInjection("83 FA 64 0F 8D")]
    private static delegate* unmanaged<CharacterManager*, int, void> deleteCharacterAtIndex;
    public static void DeleteCharacterAtIndex(int i) => deleteCharacterAtIndex(CharacterManager.Instance(), i);

    private static Bool OnLoginDetour(ContentsReplayModule* contentsReplayModule)
    {
        var ret = ContentsReplayModule.onLogin.Original(contentsReplayModule);
        if (DalamudApi.GameConfig.UiConfig.TryGetBool(nameof(UiConfigOption.CutsceneSkipIsContents), out var b) && b)
            InitializeRecordingDetour(contentsReplayModule);
        return ret;
    }

    private static void InitializeRecordingDetour(ContentsReplayModule* contentsReplayModule)
    {
        var id = contentsReplayModule->initZonePacket.contentFinderCondition;
        if (id == 0) return;

        var contentFinderCondition = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.ContentFinderCondition>()?.GetRow(id);
        if (contentFinderCondition == null) return;

        var contentType = contentFinderCondition.ContentType.Row;
        if (!whitelistedContentTypes.Contains(contentType)) return;

        contentsReplayModule->FixNextReplaySaveSlot();
        ContentsReplayModule.initializeRecording.Original(contentsReplayModule);
        contentsReplayModule->BeginRecording();

        var header = contentsReplayModule->replayHeader;
        header.localCID = 0;
        contentsReplayModule->replayHeader = header;

        if (contentDirectorOffset > 0)
            ContentDirectorTimerUpdateHook?.Enable();

        ReplayPacketManager.FlushBuffer();
    }

    public static Bool RequestPlaybackDetour(ContentsReplayModule* contentsReplayModule, byte slot)
    {
        var customSlot = slot == 100;
        FFXIVReplay.Header prevHeader = new();

        if (customSlot)
        {
            slot = 0;
            prevHeader = contentsReplayModule->savedReplayHeaders[0];
            contentsReplayModule->savedReplayHeaders[0] = lastSelectedHeader;
        }
        else
        {
            LastSelectedReplay = null;
        }

        var ret = ContentsReplayModule.requestPlayback.Original(contentsReplayModule, slot);

        if (customSlot)
            contentsReplayModule->savedReplayHeaders[0] = prevHeader;

        return ret;
    }

    private static void BeginPlaybackDetour(ContentsReplayModule* contentsReplayModule, Bool allowed)
    {
        ContentsReplayModule.beginPlayback.Original(contentsReplayModule, allowed);
        if (!allowed) return;

        ReplayManager.UnloadReplay();

        if (string.IsNullOrEmpty(LastSelectedReplay))
            ReplayManager.LoadReplay(contentsReplayModule->currentReplaySlot);
        else
            ReplayManager.LoadReplay(LastSelectedReplay);
    }

    private static void PlaybackUpdateDetour(ContentsReplayModule* contentsReplayModule)
    {
        ContentsReplayModule.playbackUpdate.Original(contentsReplayModule);

        UpdateAutoRename();

        if (contentsReplayModule->IsRecording && contentsReplayModule->chapters[0]->type == 1) // For some reason the barrier dropping in dungeons is 5, but in trials it's 1
            contentsReplayModule->chapters[0]->type = 5;

        if (!contentsReplayModule->InPlayback) return;

        SetConditionFlag(ConditionFlag.OccupiedInCutSceneEvent, false);

        ReplayManager.PlaybackUpdate(contentsReplayModule);
    }

    public static FFXIVReplay.DataSegment* GetReplayDataSegmentDetour(ContentsReplayModule* contentsReplayModule)
    {
        var segment = ReplayManager.GetReplayDataSegment(contentsReplayModule);
        return segment != null ? segment : ContentsReplayModule.getReplayDataSegment.Original(contentsReplayModule);
    }

    private static void OnSetChapterDetour(ContentsReplayModule* contentsReplayModule, byte chapter)
    {
        ContentsReplayModule.onSetChapter.Original(contentsReplayModule, chapter);
        ReplayManager.OnSetChapter(contentsReplayModule, chapter);
    }

    private delegate Bool ExecuteCommandDelegate(uint clientTrigger, int param1, int param2, int param3, int param4);
    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 8D 43 0A")]
    private static Hook<ExecuteCommandDelegate> ExecuteCommandHook;
    private static Bool ExecuteCommandDetour(uint clientTrigger, int param1, int param2, int param3, int param4)
    {
        if (!Common.ContentsReplayModule->InPlayback || clientTrigger is 201 or 1981) return ExecuteCommandHook.Original(clientTrigger, param1, param2, param3, param4); // Block GPose and Idle Camera from sending packets
        if (clientTrigger == 314) // Mimic GPose and Idle Camera ConditionFlag for plugin compatibility
            SetConditionFlag(ConditionFlag.WatchingCutscene, param1 != 0);
        return false;
    }

    private delegate Bool DisplayRecordingOnDTRBarDelegate(nint agent);
    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 44 0F B6 C0 BA 4F 00 00 00")]
    private static Hook<DisplayRecordingOnDTRBarDelegate> DisplayRecordingOnDTRBarHook;
    private static Bool DisplayRecordingOnDTRBarDetour(nint agent) => ARealmRecorded.Config.EnableRecordingIcon && Common.ContentsReplayModule->IsRecording && DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi;

    private delegate void ContentDirectorTimerUpdateDelegate(nint contentDirector);
    [HypostasisSignatureInjection("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 0F 84 ?? ?? ?? ?? A8 08")]
    private static Hook<ContentDirectorTimerUpdateDelegate> ContentDirectorTimerUpdateHook;
    private static void ContentDirectorTimerUpdateDetour(nint contentDirector)
    {
        if ((*(byte*)(contentDirector + contentDirectorOffset) & 12) == 12)
        {
            Common.ContentsReplayModule->status |= 64;
            ContentDirectorTimerUpdateHook.Disable();
        }

        ContentDirectorTimerUpdateHook.Original(contentDirector);
    }

    private delegate nint EventBeginDelegate(nint a1, nint a2);
    [HypostasisSignatureInjection("40 55 53 57 41 55 41 57 48 8D 6C 24 C9")]
    private static Hook<EventBeginDelegate> EventBeginHook;
    private static nint EventBeginDetour(nint a1, nint a2) => !Common.ContentsReplayModule->InPlayback || !DalamudApi.GameConfig.UiConfig.TryGetBool(nameof(UiConfigOption.CutsceneSkipIsContents), out var b) || !b ? EventBeginHook.Original(a1, a2) : nint.Zero;

    private static Bool ReplayPacketDetour(ContentsReplayModule* contentsReplayModule, FFXIVReplay.DataSegment* segment, byte* data) =>
        ReplayPacketManager.ReplayPacket(segment, data)
        || ContentsReplayModule.replayPacket.Original(contentsReplayModule, segment, data);

    public delegate nint FormatAddonTextTimestampDelegate(nint raptureTextModule, uint addonSheetRow, int a3, uint hours, uint minutes, uint seconds, uint a7);
    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 8D 4E 64")]
    private static Hook<FormatAddonTextTimestampDelegate> FormatAddonTextTimestampHook;
    private static nint FormatAddonTextTimestampDetour(nint raptureTextModule, uint addonSheetRow, int a3, uint hours, uint minutes, uint seconds, uint a7)
    {
        var ret = FormatAddonTextTimestampHook.Original(raptureTextModule, addonSheetRow, a3, hours, minutes, seconds, a7);
        if (addonSheetRow != 3079 || !DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi) return ret;

        // In this context, a3 is the chapter index + 1, while a7 determines the chapter type name
        var currentChapterMS = Common.ContentsReplayModule->chapters[a3 - 1]->ms;
        var nextChapterMS = Common.ContentsReplayModule->chapters[a3]->ms;
        if (nextChapterMS < currentChapterMS)
            nextChapterMS = Common.ContentsReplayModule->replayHeader.totalMS;

        var timespan = new TimeSpan(0, 0, 0, 0, (int)(nextChapterMS - currentChapterMS));
        (ret + ret.ReadCString().Length).WriteCString($" ({(int)timespan.TotalMinutes:D2}:{timespan.Seconds:D2})");

        return ret;
    }

    public static string GetReplaySlotName(int slot) => $"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat";

    private static void UpdateAutoRename()
    {
        switch (Common.ContentsReplayModule->IsRecording)
        {
            case true when !wasRecording:
                wasRecording = true;
                break;
            case false when wasRecording:
                wasRecording = false;
                DalamudApi.Framework.RunOnTick(() =>
                {
                    AutoRenameReplay();
                    Common.ContentsReplayModule->SetSavedReplayCIDs(DalamudApi.ClientState.LocalContentId);
                }, default, 30);
                break;
        }
    }

    public static FFXIVReplay* ReadReplay(string path)
    {
        var ptr = nint.Zero;
        var allocated = false;

        try
        {
            using var fs = File.OpenRead(path);

            ptr = Marshal.AllocHGlobal((int)fs.Length);
            allocated = true;

            _ = fs.Read(new Span<byte>((void*)ptr, (int)fs.Length));
        }
        catch (Exception e)
        {
            DalamudApi.LogError($"Failed to read replay {path}\n{e}");

            if (allocated)
            {
                Marshal.FreeHGlobal(ptr);
                ptr = nint.Zero;
            }
        }

        return (FFXIVReplay*)ptr;
    }

    public static FFXIVReplay? ReadReplayHeaderAndChapters(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var size = sizeof(FFXIVReplay.Header) + sizeof(FFXIVReplay.ChapterArray);
            var bytes = new byte[size];
            if (fs.Read(bytes, 0, size) != size)
                return null;
            fixed (byte* ptr = bytes)
                return *(FFXIVReplay*)ptr;
        }
        catch (Exception e)
        {
            DalamudApi.LogError($"Failed to read replay header and chapters {path}\n{e}");
            return null;
        }
    }

    public static List<(FileInfo, FFXIVReplay)> GetReplayList()
    {
        try
        {
            var directory = new DirectoryInfo(replayFolder);

            var renamedDirectory = new DirectoryInfo(autoRenamedFolder);
            if (!renamedDirectory.Exists)
            {
                if (ARealmRecorded.Config.MaxAutoRenamedReplays > 0)
                    renamedDirectory.Create();
                else
                    renamedDirectory = null;
            }

            var list = (from file in directory.GetFiles().Concat(renamedDirectory?.GetFiles() ?? Array.Empty<FileInfo>())
                    where file.Extension == ".dat"
                    let replay = ReadReplayHeaderAndChapters(file.FullName)
                    where replay is { header.IsValid: true }
                    select (file, replay.Value)
                ).ToList();

            replayList = list;
        }
        catch
        {
            replayList = new();
        }

        return replayList;
    }

    public static void RenameReplay(FileInfo file, string name)
    {
        try
        {
            file.MoveTo(Path.Combine(replayFolder, $"{name}.dat"));
        }
        catch (Exception e)
        {
            DalamudApi.PrintError($"Failed to rename replay\n{e}");
        }
    }

    public static void AutoRenameReplay()
    {
        if (ARealmRecorded.Config.MaxAutoRenamedReplays <= 0)
        {
            GetReplayList();
            return;
        }

        try
        {
            var (file, replay) = GetReplayList().MaxBy(t => t.Item1.CreationTime);

            var name = $"{bannedFileCharacters.Replace(Common.ContentsReplayModule->contentTitle.ToString(), string.Empty)} {DateTime.Now:yyyy.MM.dd HH.mm.ss}";
            file.MoveTo(Path.Combine(autoRenamedFolder, $"{name}.dat"));

            var renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            while (renamedFiles.Count > ARealmRecorded.Config.MaxAutoRenamedReplays)
            {
                DeleteReplay(renamedFiles.OrderBy(f => f.CreationTime).First());
                renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            }

            GetReplayList();

            for (int i = 0; i < 3; i++)
            {
                if (Common.ContentsReplayModule->savedReplayHeaders[i].timestamp != replay.header.timestamp) continue;
                Common.ContentsReplayModule->savedReplayHeaders[i] = new FFXIVReplay.Header();
                break;
            }
        }
        catch (Exception e)
        {
            DalamudApi.PrintError($"Failed to rename replay\n{e}");
        }
    }

    public static void DeleteReplay(FileInfo file)
    {
        try
        {
            if (ARealmRecorded.Config.MaxDeletedReplays > 0)
            {
                var deletedDirectory = new DirectoryInfo(deletedFolder);
                if (!deletedDirectory.Exists)
                    deletedDirectory.Create();

                file.MoveTo(Path.Combine(deletedFolder, file.Name), true);

                var deletedFiles = deletedDirectory.GetFiles().Where(f => f.Extension == ".dat").ToList();
                while (deletedFiles.Count > ARealmRecorded.Config.MaxDeletedReplays)
                {
                    deletedFiles.OrderBy(f => f.CreationTime).First().Delete();
                    deletedFiles = deletedDirectory.GetFiles().Where(f => f.Extension == ".dat").ToList();
                }
            }
            else
            {
                file.Delete();
            }

            GetReplayList();
        }
        catch (Exception e)
        {
            DalamudApi.PrintError($"Failed to delete replay\n{e}");
        }
    }

    public static void ArchiveReplays()
    {
        var archivableReplays = ReplayList.Where(t => !t.Item2.header.IsPlayable && t.Item1.Directory?.Name == "replay").ToArray();
        if (archivableReplays.Length == 0) return;

        var restoreBackup = true;

        try
        {
            using (var zipFileStream = new FileStream(archiveZip, FileMode.OpenOrCreate))
            using (var zipFile = new ZipArchive(zipFileStream, ZipArchiveMode.Update))
            {
                var expectedEntryCount = zipFile.Entries.Count;
                if (expectedEntryCount > 0)
                {
                    var prevPosition = zipFileStream.Position;
                    zipFileStream.Position = 0;
                    using var zipBackupFileStream = new FileStream($"{archiveZip}.BACKUP", FileMode.Create);
                    zipFileStream.CopyTo(zipBackupFileStream);
                    zipFileStream.Position = prevPosition;
                }

                foreach (var (file, _) in archivableReplays)
                {
                    zipFile.CreateEntryFromFile(file.FullName, file.Name);
                    expectedEntryCount++;
                }

                if (zipFile.Entries.Count != expectedEntryCount)
                    throw new IOException($"Number of archived replays was unexpected (Expected: {expectedEntryCount}, Actual: {zipFile.Entries.Count}) after archiving, restoring backup!");
            }

            restoreBackup = false;

            foreach (var (file, _) in archivableReplays)
                file.Delete();
        }
        catch (Exception e)
        {

            if (restoreBackup)
            {
                try
                {
                    using var zipBackupFileStream = new FileStream($"{archiveZip}.BACKUP", FileMode.Open);
                    using var zipFileStream = new FileStream(archiveZip, FileMode.Create);
                    zipBackupFileStream.CopyTo(zipFileStream);
                }
                catch { }
            }

            DalamudApi.PrintError($"Failed to archive replays\n{e}");
        }

        GetReplayList();
    }

    public static void SetDutyRecorderMenuSelection(nint agent, byte slot)
    {
        *(byte*)(agent + 0x2C) = slot;
        *(byte*)(agent + 0x2A) = 1;
        DisplaySelectedDutyRecording(agent);
    }

    public static void SetDutyRecorderMenuSelection(nint agent, string path, FFXIVReplay.Header header)
    {
        header.localCID = DalamudApi.ClientState.LocalContentId;
        LastSelectedReplay = path;
        lastSelectedHeader = header;
        var prevHeader = Common.ContentsReplayModule->savedReplayHeaders[0];
        Common.ContentsReplayModule->savedReplayHeaders[0] = header;
        SetDutyRecorderMenuSelection(agent, 0);
        Common.ContentsReplayModule->savedReplayHeaders[0] = prevHeader;
        *(byte*)(agent + 0x2C) = 100;
    }

    public static void CopyReplayIntoSlot(nint agent, FileInfo file, FFXIVReplay.Header header, byte slot)
    {
        if (slot > 2) return;

        try
        {
            file.CopyTo(Path.Combine(replayFolder, GetReplaySlotName(slot)), true);
            header.localCID = DalamudApi.ClientState.LocalContentId;
            Common.ContentsReplayModule->savedReplayHeaders[slot] = header;
            SetDutyRecorderMenuSelection(agent, slot);
            GetReplayList();
        }
        catch (Exception e)
        {
            DalamudApi.PrintError($"Failed to copy replay to slot {slot + 1}\n{e}");
        }
    }

    public static void OpenReplayFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = replayFolder,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public static void ToggleWaymarks() => *waymarkToggle ^= 2;

    public static void SetConditionFlag(ConditionFlag flag, bool b) => *(bool*)(DalamudApi.Condition.Address + (int)flag) = b;

    [Conditional("DEBUG")]
    public static void ReadPackets(string path)
    {
        var replay = ReadReplay(path);
        if (replay == null) return;

        var opcodeCount = new Dictionary<uint, uint>();
        var opcodeLengths = new Dictionary<uint, uint>();

        var offset = 0u;
        var totalPackets = 0u;

        FFXIVReplay.DataSegment* segment;
        while ((segment = replay->GetDataSegment(offset)) != null)
        {
            opcodeCount.TryGetValue(segment->opcode, out var count);
            opcodeCount[segment->opcode] = ++count;

            opcodeLengths[segment->opcode] = segment->dataLength;
            offset += segment->Length;
            ++totalPackets;
        }

        Marshal.FreeHGlobal((nint)replay);

        DalamudApi.LogInfo("-------------------");
        DalamudApi.LogInfo($"Opcodes inside: {path} (Total: [{opcodeCount.Count}] {totalPackets})");
        foreach (var (opcode, count) in opcodeCount)
            DalamudApi.LogInfo($"[{opcode:X}] {count} ({opcodeLengths[opcode]})");
        DalamudApi.LogInfo("-------------------");
    }

    public static void Initialize()
    {
        if (!Common.IsValid(Common.ContentsReplayModule))
            throw new ApplicationException($"{nameof(Common.ContentsReplayModule)} is not initialized!");

        ContentsReplayModule.onLogin.CreateHook(OnLoginDetour);
        ContentsReplayModule.initializeRecording.CreateHook(InitializeRecordingDetour);
        ContentsReplayModule.playbackUpdate.CreateHook(PlaybackUpdateDetour);
        ContentsReplayModule.requestPlayback.CreateHook(RequestPlaybackDetour);
        ContentsReplayModule.beginPlayback.CreateHook(BeginPlaybackDetour);
        ContentsReplayModule.getReplayDataSegment.CreateHook(GetReplayDataSegmentDetour);
        ContentsReplayModule.onSetChapter.CreateHook(OnSetChapterDetour);
        ContentsReplayModule.replayPacket.CreateHook(ReplayPacketDetour);

        Common.ContentsReplayModule->SetSavedReplayCIDs(DalamudApi.ClientState.LocalContentId);

        if (Common.ContentsReplayModule->InPlayback && Common.ContentsReplayModule->fileStream != nint.Zero && *(long*)Common.ContentsReplayModule->fileStream == 0)
            ReplayManager.LoadReplay(ARealmRecorded.Config.LastLoadedReplay);
    }

    public static void Dispose()
    {
        if (Common.ContentsReplayModule != null)
            Common.ContentsReplayModule->SetSavedReplayCIDs(0);

        ReplayManager.Dispose();
    }
}