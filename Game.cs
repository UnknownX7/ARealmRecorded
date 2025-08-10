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
    public static readonly string replayFolder = Path.Combine(Framework.Instance()->UserPathString, "replay");
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

    private static readonly HashSet<uint> whitelistedContentTypes = [ 1, 2, 3, 4, 5, 9, 28, 29, 30, 37, 38 ]; // 22 Event, 26 Eureka, 27 Carnivale

    private static readonly AsmPatch alwaysRecordPatch = new("24 06 3C 02 75 29", [ 0xEB, 0x25 ], true);
    private static readonly AsmPatch removeRecordReadyToastPatch = new("BA CB 07 00 00 48 8B CF E8", [ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 ], true);
    private static readonly AsmPatch seIsABunchOfClownsPatch = new("F6 40 78 02 74 04 B0 01 EB 02 32 C0 40 84 FF", [ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 ], true);
    private static readonly AsmPatch instantFadeOutPatch = new("44 8D 47 0A 33 D2", [ null, null, 0x07, 0x90 ], true); // lea r8d, [rdi+0A] -> lea r8d, [rdi]
    private static readonly AsmPatch instantFadeInPatch = new("44 8D 42 0A 41 FF 92 ?? ?? 00 00 48 8B 5C 24", [ null, null, null, 0x01 ], true); // lea r8d, [rdx+0A] -> lea r8d, [rdx+01]
    public static readonly AsmPatch replaceLocalPlayerNamePatch = new("75 ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? F6 05", [ 0x90, 0x90 ], ARealmRecorded.Config.EnableHideOwnName);

    [HypostasisSignatureInjection("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 3B 48 8B 0D", Static = true, Offset = 0x48)]
    private static byte* waymarkToggle; // Actually a uint, but only seems to use the first 2 bits

    public static bool IsWaymarkVisible => (*waymarkToggle & 2) == 0;

    [HypostasisSignatureInjection("?? ?? 00 00 01 75 74 85 FF 75 07 E8")]
    public static short contentDirectorOffset;

    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 39 6E 34")]
    private static delegate* unmanaged<nint, void> displaySelectedDutyRecording;
    public static void DisplaySelectedDutyRecording(nint agent) => displaySelectedDutyRecording(agent);

    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 33 D2 48 8D 47 50")] // Client::Game::Character::CharacterManager_DeleteCharacterAtIndex
    private static delegate* unmanaged<CharacterManager*, int, void> deleteCharacterAtIndex;
    public static void DeleteCharacterAtIndex(int i) => deleteCharacterAtIndex(CharacterManager.Instance(), i);

    private static void OnZoneInPacketDetour(ContentsReplayModule* contentsReplayModule, uint gameObjectID, nint packet)
    {
        ContentsReplayModule.onZoneInPacket.Original(contentsReplayModule, gameObjectID, packet);
        if ((contentsReplayModule->status & 1) == 0) return;

        if (DalamudApi.GameConfig.UiConfig.TryGetBool(nameof(UiConfigOption.CutsceneSkipIsContents), out var b) && b)
            InitializeRecordingDetour(contentsReplayModule);
    }

    private static void InitializeRecordingDetour(ContentsReplayModule* contentsReplayModule)
    {
        var id = contentsReplayModule->initZonePacket.contentFinderCondition;
        if (id == 0) return;

        var contentFinderCondition = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>().GetRowOrDefault(id);
        if (contentFinderCondition == null) return;

        var contentType = contentFinderCondition.Value.ContentType.RowId;
        if (!whitelistedContentTypes.Contains(contentType)) return;

        contentsReplayModule->FixNextReplaySaveSlot();
        ContentsReplayModule.initializeRecording.Original(contentsReplayModule);
        contentsReplayModule->BeginRecording();

        var header = contentsReplayModule->replayHeader;
        header.localCID = 0;
        contentsReplayModule->replayHeader = header;

        if (contentDirectorOffset > 0)
            ContentDirectorSynchronizeHook?.Enable();

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

    private static void ReceiveActorControlPacketDetour(ContentsReplayModule* contentsReplayModule, uint gameObjectID, nint packet)
    {
        ContentsReplayModule.receiveActorControlPacket.Original(contentsReplayModule, gameObjectID, packet);
        if (*(ushort*)packet != 931 || !*(Bool*)(packet + 4)) return;

        ReplayManager.UnloadReplay();

        if (string.IsNullOrEmpty(LastSelectedReplay))
            ReplayManager.LoadReplay(contentsReplayModule->currentReplaySlot);
        else
            ReplayManager.LoadReplay(LastSelectedReplay);
    }

    private static void PlaybackUpdateDetour(ContentsReplayModule* contentsReplayModule)
    {
        GetReplayDataSegmentHook?.Enable();
        ContentsReplayModule.playbackUpdate.Original(contentsReplayModule);
        GetReplayDataSegmentHook?.Disable();

        UpdateAutoRename();

        if (contentsReplayModule->IsRecording && contentsReplayModule->chapters[0]->type == 1) // For some reason the barrier dropping in dungeons is 5, but in trials it's 1
            contentsReplayModule->chapters[0]->type = 5;

        if (!contentsReplayModule->InPlayback) return;

        SetConditionFlag(ConditionFlag.OccupiedInCutSceneEvent, false);

        ReplayManager.PlaybackUpdate(contentsReplayModule);
    }

    private static AsmPatch createGetReplaySegmentHookPatch;
    private static Hook<ContentsReplayModule.GetReplayDataSegmentDelegate> GetReplayDataSegmentHook;
    public static FFXIVReplay.DataSegment* GetReplayDataSegmentDetour(ContentsReplayModule* contentsReplayModule) => ReplayManager.GetReplayDataSegment(contentsReplayModule);

    private static void OnSetChapterDetour(ContentsReplayModule* contentsReplayModule, byte chapter)
    {
        ContentsReplayModule.onSetChapter.Original(contentsReplayModule, chapter);
        ReplayManager.OnSetChapter(contentsReplayModule, chapter);
    }

    private delegate Bool ExecuteCommandDelegate(uint clientTrigger, int param1, int param2, int param3, int param4);
    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 8D 46 0A")]
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

    private delegate void ContentDirectorSynchronizeDelegate(nint contentDirector);
    [HypostasisSignatureInjection("40 53 55 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 81 ?? ?? ?? ??")] // Client::Game::InstanceContent::ContentDirector_Synchronize
    private static Hook<ContentDirectorSynchronizeDelegate> ContentDirectorSynchronizeHook;
    private static void ContentDirectorSynchronizeDetour(nint contentDirector)
    {
        if ((*(byte*)(contentDirector + contentDirectorOffset) & 12) == 12)
        {
            Common.ContentsReplayModule->status |= 64;
            ContentDirectorSynchronizeHook.Disable();
        }

        ContentDirectorSynchronizeHook.Original(contentDirector);
    }

    private delegate nint EventBeginDelegate(nint a1, nint a2);
    [HypostasisSignatureInjection("40 53 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 59 08")] // Client::Game::Event::EventSceneModuleUsualImpl_PlayCutScene
    private static Hook<EventBeginDelegate> EventBeginHook;
    private static nint EventBeginDetour(nint a1, nint a2) => !Common.ContentsReplayModule->InPlayback || !DalamudApi.GameConfig.UiConfig.TryGetBool(nameof(UiConfigOption.CutsceneSkipIsContents), out var b) || !b ? EventBeginHook.Original(a1, a2) : nint.Zero;

    private static Bool ReplayPacketDetour(ContentsReplayModule* contentsReplayModule, FFXIVReplay.DataSegment* segment, byte* data) =>
        ReplayPacketManager.ReplayPacket(segment, data)
        || ContentsReplayModule.replayPacket.Original(contentsReplayModule, segment, data);

    public delegate nint FormatAddonTextTimestampDelegate(nint raptureTextModule, uint addonSheetRow, int a3, uint hours, uint minutes, uint seconds, uint a7);
    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 48 8B D0 8D 4F 0E")]
    private static Hook<FormatAddonTextTimestampDelegate> FormatAddonTextTimestampHook;
    private static nint FormatAddonTextTimestampDetour(nint raptureTextModule, uint addonSheetRow, int a3, uint hours, uint minutes, uint seconds, uint a7)
    {
        var ret = FormatAddonTextTimestampHook.Original(raptureTextModule, addonSheetRow, a3, hours, minutes, seconds, a7);

        try
        {
            if (a3 > 64 || addonSheetRow != 3079 || !DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi) return ret;

            // In this context, a3 is the chapter index + 1, while a7 determines the chapter type name
            var currentChapterMS = Common.ContentsReplayModule->chapters[a3 - 1]->ms;
            var nextChapterMS = a3 < 64 ? Common.ContentsReplayModule->chapters[a3]->ms : Common.ContentsReplayModule->replayHeader.totalMS;
            if (nextChapterMS < currentChapterMS)
                nextChapterMS = Common.ContentsReplayModule->replayHeader.totalMS;

            var timespan = new TimeSpan(0, 0, 0, 0, (int)(nextChapterMS - currentChapterMS));
            (ret + ret.ReadCString().Length).WriteCString($" ({(int)timespan.TotalMinutes:D2}:{timespan.Seconds:D2})");
        }
        catch (Exception e)
        {
            DalamudApi.LogError(e.ToString());
        }

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

            var list = (from file in directory.GetFiles().Concat(renamedDirectory?.GetFiles() ?? [])
                    where file.Extension == ".dat"
                    let replay = ReadReplayHeaderAndChapters(file.FullName)
                    where replay is { header.IsValid: true }
                    select (file, replay.Value)
                ).ToList();

            replayList = list;
        }
        catch
        {
            replayList = [];
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
            var (file, replay) = GetReplayList().Where(t => t.Item1.Name.StartsWith("FFXIV_")).MaxBy(t => t.Item1.LastWriteTime);

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

        // Utilize an unused function to act as a hook
        var address = DalamudApi.SigScanner.ScanModule("48 39 43 38 0F 83 ?? ?? ?? ?? 48 8B 4B 30");
        var functionAddress = ContentsReplayModule.onLogin.Address;
        GetReplayDataSegmentHook = DalamudApi.GameInteropProvider.HookFromAddress<ContentsReplayModule.GetReplayDataSegmentDelegate>(functionAddress, GetReplayDataSegmentDetour);
        createGetReplaySegmentHookPatch = new(address,
            [
               0x48, 0x8B, 0xCB, // mov rcx, rbx
               0xE8, .. BitConverter.GetBytes((int)(functionAddress - (address + 0x8))), // call onLogin
               0x4C, 0x8B, 0xF0, // mov r14, rax
               0xEB, 0x3A, // jmp
               0x90
            ],
            true);

        ContentsReplayModule.onZoneInPacket.CreateHook(OnZoneInPacketDetour);
        ContentsReplayModule.initializeRecording.CreateHook(InitializeRecordingDetour);
        ContentsReplayModule.playbackUpdate.CreateHook(PlaybackUpdateDetour);
        ContentsReplayModule.requestPlayback.CreateHook(RequestPlaybackDetour);
        ContentsReplayModule.receiveActorControlPacket.CreateHook(ReceiveActorControlPacketDetour);
        ContentsReplayModule.onSetChapter.CreateHook(OnSetChapterDetour);
        ContentsReplayModule.replayPacket.CreateHook(ReplayPacketDetour);

        Common.ContentsReplayModule->SetSavedReplayCIDs(DalamudApi.ClientState.LocalContentId);

        if (Common.ContentsReplayModule->InPlayback && Common.ContentsReplayModule->fileStream != nint.Zero && *(long*)Common.ContentsReplayModule->fileStream == 0)
            ReplayManager.LoadReplay(ARealmRecorded.Config.LastLoadedReplay);
    }

    public static void Dispose()
    {
        createGetReplaySegmentHookPatch?.Dispose();
        GetReplayDataSegmentHook?.Dispose();

        if (Common.ContentsReplayModule != null)
            Common.ContentsReplayModule->SetSavedReplayCIDs(0);

        ReplayManager.Dispose();
    }
}
