using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Hypostasis.Game.Structures;

namespace ARealmRecorded;

[HypostasisInjection]
public static unsafe class Game
{
    private static readonly string replayFolder = Path.Combine(Framework.Instance()->UserPath, "replay");
    private static readonly string autoRenamedFolder = Path.Combine(replayFolder, "autorenamed");
    private static readonly string archiveZip = Path.Combine(replayFolder, "archive.zip");
    private static readonly string deletedFolder = Path.Combine(replayFolder, "deleted");
    private static FFXIVReplay.ReplayFile* loadedReplay = null;

    public static string LastSelectedReplay { get; private set; }
    private static FFXIVReplay.Header lastSelectedHeader;

    private static int quickLoadChapter = -1;
    private static int seekingChapter = 0;
    private static uint seekingOffset = 0;

    private static int currentRecordingSlot = -1;
    private static readonly Regex bannedFolderCharacters = new("[\\\\\\/:\\*\\?\"\\<\\>\\|\u0000-\u001F]");

    private static readonly HashSet<uint> whitelistedContentTypes = new() { 1, 2, 3, 4, 5, 9, 28, 29, 30 }; // 22 Event, 26 Eureka, 27 Carnivale

    private static List<(FileInfo, FFXIVReplay.ReplayFile)> replayList;
    public static List<(FileInfo, FFXIVReplay.ReplayFile)> ReplayList
    {
        get => replayList ?? GetReplayList();
        set => replayList = value;
    }

    private const int RsfSize = 0x48;
    private const ushort RsfOpcode = 0xF002;
    private static readonly List<byte[]> rsfBuffer = new();
    private const ushort RsvOpcode = 0xF001;
    private static readonly List<byte[]> rsvBuffer = new();

    private static readonly AsmPatch alwaysRecordPatch = new("A8 04 75 27 A8 02 74 23 48 8B", new byte?[] { 0xEB, 0x21 }, true); // 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90
    private static readonly AsmPatch removeRecordReadyToastPatch = new("BA CB 07 00 00 48 8B CF E8", new byte?[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly AsmPatch removeProcessingLimitPatch = new("41 FF C6 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84", new byte?[] { 0x90, 0x90, 0x90 }, true);
    private static readonly AsmPatch removeProcessingLimitPatch2 = new("77 57 48 8B 0D ?? ?? ?? ?? 33 C0", new byte?[] { 0x90, 0x90 }, true);
    private static readonly AsmPatch forceFastForwardPatch = new("0F 83 ?? ?? ?? ?? 0F B7 47 02 4C 8D 47 0C", new byte?[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });

    public static readonly AsmPatch replaceLocalPlayerNamePatch = new("F6 05 ?? ?? ?? ?? 04 74 ?? 45 33 C0 33 D2 49 8B CE E8 ?? ?? ?? ?? 84 C0 75 ??", "?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? 90 90", ARealmRecorded.Config.EnableHideOwnName);

    [HypostasisSignatureInjection("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 0E", Static = true, Offset = 0x48)]
    private static byte* waymarkToggle; // Actually a uint, but only seems to use the first 2 bits

    public static bool IsWaymarkVisible => (*waymarkToggle & 2) == 0;

    [HypostasisSignatureInjection("?? ?? 00 00 01 75 74 85 FF 75 07 E8")]
    public static short contentDirectorOffset;

    [HypostasisSignatureInjection("48 89 5C 24 10 57 48 81 EC 70 04 00 00")]
    private static delegate* unmanaged<nint, void> displaySelectedDutyRecording;
    public static void DisplaySelectedDutyRecording(nint agent) => displaySelectedDutyRecording(agent);

    private static void InitializeRecordingDetour(FFXIVReplay* ffxivReplay)
    {
        var id = ffxivReplay->initZonePacket.contentFinderCondition;
        if (id == 0) return;

        var contentFinderCondition = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.ContentFinderCondition>()?.GetRow(id);
        if (contentFinderCondition == null) return;

        var contentType = contentFinderCondition.ContentType.Row;
        if (!whitelistedContentTypes.Contains(contentType)) return;

        FixNextReplaySaveSlot();
        FFXIVReplay.initializeRecording.Original(ffxivReplay);
        ffxivReplay->BeginRecording();

        var header = ffxivReplay->replayHeader;
        header.localCID = 0;
        ffxivReplay->replayHeader = header;

        if (contentDirectorOffset > 0)
            ContentDirectorTimerUpdateHook?.Enable();

        FlushRsvRsfBuffers(); // TODO: Look into potential issue with packets received from The Unending Journey being added to replays
    }

    public static Bool RequestPlaybackDetour(FFXIVReplay* ffxivReplay, byte slot)
    {
        var customSlot = slot == 100;
        FFXIVReplay.Header prevHeader = new();

        if (customSlot)
        {
            slot = 0;
            prevHeader = ffxivReplay->savedReplayHeaders[0];
            ffxivReplay->savedReplayHeaders[0] = lastSelectedHeader;
        }
        else
        {
            LastSelectedReplay = null;
        }

        var ret = FFXIVReplay.requestPlayback.Original(ffxivReplay, slot);

        if (customSlot)
            ffxivReplay->savedReplayHeaders[0] = prevHeader;

        return ret;
    }

    private static void BeginPlaybackDetour(FFXIVReplay* ffxivReplay, Bool allowed)
    {
        FFXIVReplay.beginPlayback.Original(ffxivReplay, allowed);
        if (!allowed) return;

        UnloadReplay();

        if (string.IsNullOrEmpty(LastSelectedReplay))
            LoadReplay(ffxivReplay->currentReplaySlot);
        else
            LoadReplay(LastSelectedReplay);
    }

    private static void PlaybackUpdateDetour(FFXIVReplay* ffxivReplay)
    {
        FFXIVReplay.playbackUpdate.Original(ffxivReplay);

        UpdateAutoRename();

        if (ffxivReplay->IsRecording && ffxivReplay->chapters[0]->type == 1) // For some reason the barrier dropping in dungeons is 5, but in trials it's 1
            ffxivReplay->chapters[0]->type = 5;

        if (!ffxivReplay->InPlayback) return;

        SetConditionFlag(ConditionFlag.OccupiedInCutSceneEvent, false);

        if (loadedReplay == null) return;

        ffxivReplay->dataLoadType = 0;
        ffxivReplay->dataOffset = 0;

        if (quickLoadChapter < 2) return;

        var seekedTime = ffxivReplay->chapters[seekingChapter]->ms / 1000f;
        if (seekedTime > ffxivReplay->seek) return;

        DoQuickLoad();
    }

    public static FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDetour(FFXIVReplay* ffxivReplay)
    {
        // Needs to be here to prevent infinite looping
        if (seekingOffset > 0 && seekingOffset <= ffxivReplay->overallDataOffset)
        {
            forceFastForwardPatch.Disable();
            seekingOffset = 0;
        }

        // Absurdly hacky, but it works
        if (!ARealmRecorded.Config.EnableQuickLoad || ARealmRecorded.Config.MaxSeekDelta <= 100 || ffxivReplay->seekDelta >= ARealmRecorded.Config.MaxSeekDelta)
            removeProcessingLimitPatch2.Disable();
        else
            removeProcessingLimitPatch2.Enable();

        return loadedReplay == null ? FFXIVReplay.getReplayDataSegment.Original(ffxivReplay) : loadedReplay->GetDataSegment((uint)ffxivReplay->overallDataOffset);
    }

    private static void OnSetChapterDetour(FFXIVReplay* ffxivReplay, byte chapter)
    {
        FFXIVReplay.onSetChapter.Original(ffxivReplay, chapter);

        if (!ARealmRecorded.Config.EnableQuickLoad || chapter <= 0 || ffxivReplay->chapters.length < 2) return;

        quickLoadChapter = chapter;
        seekingChapter = -1;
        DoQuickLoad();
    }

    private delegate Bool ExecuteCommandDelegate(uint clientTrigger, int param1, int param2, int param3, int param4);
    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 8D 43 0A")]
    private static Hook<ExecuteCommandDelegate> ExecuteCommandHook;
    private static Bool ExecuteCommandDetour(uint clientTrigger, int param1, int param2, int param3, int param4)
    {
        if (!Common.FFXIVReplay->InPlayback || clientTrigger is 201 or 1981) return ExecuteCommandHook.Original(clientTrigger, param1, param2, param3, param4); // Block GPose and Idle Camera from sending packets
        if (clientTrigger == 314) // Mimic GPose and Idle Camera ConditionFlag for plugin compatibility
            SetConditionFlag(ConditionFlag.WatchingCutscene, param1 != 0);
        return false;
    }

    private delegate Bool DisplayRecordingOnDTRBarDelegate(nint agent);
    [HypostasisSignatureInjection("E8 ?? ?? ?? ?? 44 0F B6 C0 BA 4F 00 00 00")]
    private static Hook<DisplayRecordingOnDTRBarDelegate> DisplayRecordingOnDTRBarHook;
    private static Bool DisplayRecordingOnDTRBarDetour(nint agent) => ARealmRecorded.Config.EnableRecordingIcon && Common.FFXIVReplay->IsRecording && DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi;

    private delegate void ContentDirectorTimerUpdateDelegate(nint contentDirector);
    [HypostasisSignatureInjection("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 0F 84 ?? ?? ?? ?? A8 08")]
    private static Hook<ContentDirectorTimerUpdateDelegate> ContentDirectorTimerUpdateHook;
    private static void ContentDirectorTimerUpdateDetour(nint contentDirector)
    {
        if ((*(byte*)(contentDirector + contentDirectorOffset) & 12) == 12)
        {
            Common.FFXIVReplay->status |= 64;
            ContentDirectorTimerUpdateHook.Disable();
        }

        ContentDirectorTimerUpdateHook.Original(contentDirector);
    }

    private delegate nint EventBeginDelegate(nint a1, nint a2);
    [HypostasisSignatureInjection("40 55 53 57 41 55 41 57 48 8D 6C 24 C9")]
    private static Hook<EventBeginDelegate> EventBeginHook;
    private static nint EventBeginDetour(nint a1, nint a2) => !Common.FFXIVReplay->InPlayback || ConfigModule.Instance()->GetIntValue(ConfigOption.CutsceneSkipIsContents) == 0 ? EventBeginHook.Original(a1, a2) : nint.Zero;

    public delegate Bool RsvReceiveDelegate(nint data);
    [HypostasisSignatureInjection("44 8B 09 4C 8D 41 34")]
    private static Hook<RsvReceiveDelegate> RsvReceiveHook;
    private static Bool RsvReceiveDetour(nint data)
    {
        var size = *(int*)data; // Value size
        var length = size + 0x4 + 0x30; // Package size
        rsvBuffer.Add(MemoryHelper.ReadRaw(data, length));
        return RsvReceiveHook.Original(data);
    }

    public delegate Bool RsfReceiveDelegate(nint data);
    [HypostasisSignatureInjection("48 8B 11 4C 8D 41 08")]
    private static Hook<RsfReceiveDelegate> RsfReceiveHook;
    private static Bool RsfReceiveDetour(nint data)
    {
        rsfBuffer.Add(MemoryHelper.ReadRaw(data, RsfSize));
        return RsfReceiveHook.Original(data);
    }

    private static void FlushRsvRsfBuffers()
    {
        if (Common.FFXIVReplay->IsSavingPackets)
        {
            //PluginLog.Debug($"Recording {rsfBuffer.Count} RSF packets");
            foreach (var rsf in rsfBuffer)
            {
                fixed (byte* data = rsf)
                    Common.FFXIVReplay->WritePacket(0xE000_0000, RsfOpcode, data, (ushort)rsf.Length);
            }

            //PluginLog.Debug($"Recording {rsvBuffer.Count} RSV packets");
            foreach (var rsv in rsvBuffer)
            {
                fixed (byte* data = rsv)
                    Common.FFXIVReplay->WritePacket(0xE000_0000, RsvOpcode, data, (ushort)rsv.Length);
            }
        }

        rsfBuffer.Clear();
        rsvBuffer.Clear();
    }

    private static Bool ReplayPacketDetour(FFXIVReplay* ffxivReplay, FFXIVReplay.ReplayDataSegment* segment, byte* data)
    {
        //PluginLog.Debug($"Dispatch:0x{opcode:X}");
        switch (segment->opcode) {
            case RsvOpcode:
                RsvReceiveHook.Original((nint)data);
                break;
            case RsfOpcode:
                RsfReceiveHook.Original((nint)data);
                break;
        }
        return FFXIVReplay.replayPacket.Original(ffxivReplay, segment, data);
    }

    public static string GetReplaySlotName(int slot) => $"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat";

    private static void UpdateAutoRename()
    {
        switch (Common.FFXIVReplay->IsRecording)
        {
            case true when currentRecordingSlot < 0:
                currentRecordingSlot = Common.FFXIVReplay->nextReplaySaveSlot;
                break;
            case false when currentRecordingSlot >= 0:
                AutoRenameReplay();
                currentRecordingSlot = -1;
                SetSavedReplayCIDs(DalamudApi.ClientState.LocalContentId);
                break;
        }
    }

    public static bool LoadReplay(int slot) => LoadReplay(Path.Combine(replayFolder, GetReplaySlotName(slot)));

    public static bool LoadReplay(string path)
    {
        var newReplay = ReadReplay(path);
        if (newReplay == null) return false;

        if (loadedReplay != null)
            Marshal.FreeHGlobal((nint)loadedReplay);

        loadedReplay = newReplay;
        Common.FFXIVReplay->replayHeader = loadedReplay->header;
        Common.FFXIVReplay->chapters = loadedReplay->chapters;
        Common.FFXIVReplay->dataLoadType = 0;

        ARealmRecorded.Config.LastLoadedReplay = path;
        return true;
    }

    public static bool UnloadReplay()
    {
        if (loadedReplay == null) return false;
        Marshal.FreeHGlobal((nint)loadedReplay);
        loadedReplay = null;
        return true;
    }

    public static FFXIVReplay.ReplayFile* ReadReplay(string path)
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

        return (FFXIVReplay.ReplayFile*)ptr;
    }

    public static FFXIVReplay.ReplayFile? ReadReplayHeaderAndChapters(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var size = sizeof(FFXIVReplay.Header) + sizeof(FFXIVReplay.ChapterArray);
            var bytes = new byte[size];
            if (fs.Read(bytes, 0, size) != size)
                return null;
            fixed (byte* ptr = &bytes[0])
                return *(FFXIVReplay.ReplayFile*)ptr;
        }
        catch (Exception e)
        {
            DalamudApi.LogError($"Failed to read replay header and chapters {path}\n{e}");
            return null;
        }
    }

    public static void FixNextReplaySaveSlot()
    {
        if (ARealmRecorded.Config.MaxAutoRenamedReplays <= 0 && !Common.FFXIVReplay->savedReplayHeaders[Common.FFXIVReplay->nextReplaySaveSlot].IsLocked) return;

        for (byte i = 0; i < 3; i++)
        {
            if (i != 2)
            {
                var header = Common.FFXIVReplay->savedReplayHeaders[i];
                if (header.IsLocked) continue;
            }

            Common.FFXIVReplay->nextReplaySaveSlot = i;
            return;
        }
    }

    public static byte FindPreviousChapterType(byte chapter, byte type)
    {
        for (byte i = chapter; i > 0; i--)
            if (Common.FFXIVReplay->chapters[i]->type == type) return i;
        return 0;
    }

    public static byte FindPreviousChapterType(byte type) => FindPreviousChapterType(GetCurrentChapter(), type);

    public static byte FindNextChapterType(byte chapter, byte type)
    {
        for (byte i = (byte)(chapter + 1); i < Common.FFXIVReplay->chapters.length; i++)
            if (Common.FFXIVReplay->chapters[i]->type == type) return i;
        return 0;
    }

    public static byte FindNextChapterType(byte type) => FindNextChapterType(GetCurrentChapter(), type);

    public static byte GetPreviousStartChapter(byte chapter)
    {
        var foundPreviousStart = false;
        for (byte i = chapter; i > 0; i--)
        {
            if (Common.FFXIVReplay->chapters[i]->type != 2) continue;

            if (foundPreviousStart)
                return i;
            foundPreviousStart = true;
        }
        return 0;
    }

    public static byte GetPreviousStartChapter() => GetPreviousStartChapter(GetCurrentChapter());

    public static byte FindPreviousChapterFromTime(uint ms)
    {
        for (byte i = (byte)(Common.FFXIVReplay->chapters.length - 1); i > 0; i--)
            if (Common.FFXIVReplay->chapters[i]->ms <= ms) return i;
        return 0;
    }

    public static byte GetCurrentChapter() => FindPreviousChapterFromTime((uint)(Common.FFXIVReplay->seek * 1000));

    public static FFXIVReplay.ReplayDataSegment* FindNextDataSegment(uint ms, out uint offset)
    {
        offset = 0;

        FFXIVReplay.ReplayDataSegment* segment;
        while ((segment = loadedReplay->GetDataSegment(offset)) != null)
        {
            if (segment->ms >= ms) return segment;
            offset += segment->Length;
        }

        return null;
    }

    public static void JumpToChapter(byte chapter)
    {
        var jumpChapter = Common.FFXIVReplay->chapters[chapter];
        if (jumpChapter == null) return;
        Common.FFXIVReplay->overallDataOffset = jumpChapter->offset;
        Common.FFXIVReplay->seek = jumpChapter->ms / 1000f;
    }

    public static void JumpToTime(uint ms)
    {
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;
        Common.FFXIVReplay->overallDataOffset = offset;
        Common.FFXIVReplay->seek = segment->ms / 1000f;
    }

    public static void JumpToTimeBeforeChapter(byte chapter, uint ms)
    {
        var jumpChapter = Common.FFXIVReplay->chapters[chapter];
        if (jumpChapter == null) return;
        JumpToTime(jumpChapter->ms > ms ? jumpChapter->ms - ms : 0);
    }

    public static void SeekToTime(uint ms)
    {
        if (Common.FFXIVReplay->IsLoadingChapter) return;

        var prevChapter = FindPreviousChapterFromTime(ms);
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;

        seekingOffset = offset;
        forceFastForwardPatch.Enable();
        if (Common.FFXIVReplay->seek * 1000 < segment->ms && prevChapter == GetCurrentChapter())
            FFXIVReplay.onSetChapter.Original(Common.FFXIVReplay, prevChapter);
        else
            Common.FFXIVReplay->SetChapter(prevChapter);
    }

    public static void ReplaySection(byte from, byte to)
    {
        if (from != 0 && Common.FFXIVReplay->overallDataOffset < Common.FFXIVReplay->chapters[from]->offset)
            JumpToChapter(from);

        seekingChapter = to;
        if (seekingChapter >= quickLoadChapter)
            quickLoadChapter = -1;
    }

    public static void DoQuickLoad()
    {
        if (seekingChapter < 0)
        {
            ReplaySection(0, 1);
            return;
        }

        var nextEvent = FindNextChapterType((byte)seekingChapter, 4);
        if (nextEvent != 0 && nextEvent < quickLoadChapter - 1)
        {
            var nextCountdown = FindNextChapterType(nextEvent, 1);
            if (nextCountdown == 0 || nextCountdown > nextEvent + 2)
                nextCountdown = (byte)(nextEvent + 1);
            ReplaySection(nextEvent, nextCountdown);
            return;
        }

        ReplaySection(GetPreviousStartChapter((byte)quickLoadChapter), (byte)quickLoadChapter);
    }

    public static List<(FileInfo, FFXIVReplay.ReplayFile)> GetReplayList()
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
            var fileName = GetReplaySlotName(currentRecordingSlot);
            var (file, _) = GetReplayList().First(t => t.Item1.Name == fileName);

            var name = $"{bannedFolderCharacters.Replace(Common.FFXIVReplay->contentTitle.ToString(), string.Empty)} {DateTime.Now:yyyy.MM.dd HH.mm.ss}";
            file.MoveTo(Path.Combine(autoRenamedFolder, $"{name}.dat"));

            var renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            while (renamedFiles.Count > ARealmRecorded.Config.MaxAutoRenamedReplays)
            {
                DeleteReplay(renamedFiles.OrderBy(f => f.CreationTime).First());
                renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            }

            GetReplayList();
            Common.FFXIVReplay->savedReplayHeaders[currentRecordingSlot] = new FFXIVReplay.Header();
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
        var prevHeader = Common.FFXIVReplay->savedReplayHeaders[0];
        Common.FFXIVReplay->savedReplayHeaders[0] = header;
        SetDutyRecorderMenuSelection(agent, 0);
        Common.FFXIVReplay->savedReplayHeaders[0] = prevHeader;
        *(byte*)(agent + 0x2C) = 100;
    }

    public static void CopyReplayIntoSlot(nint agent, FileInfo file, FFXIVReplay.Header header, byte slot)
    {
        if (slot > 2) return;

        try
        {
            file.CopyTo(Path.Combine(replayFolder, GetReplaySlotName(slot)), true);
            header.localCID = DalamudApi.ClientState.LocalContentId;
            Common.FFXIVReplay->savedReplayHeaders[slot] = header;
            SetDutyRecorderMenuSelection(agent, slot);
            GetReplayList();
        }
        catch (Exception e)
        {
            DalamudApi.PrintError($"Failed to copy replay to slot {slot + 1}\n{e}");
        }
    }

    public static void SetSavedReplayCIDs(ulong cID)
    {
        if (Common.FFXIVReplay->savedReplayHeaders == null) return;

        for (int i = 0; i < 3; i++)
        {
            var header = Common.FFXIVReplay->savedReplayHeaders[i];
            if (!header.IsValid) continue;
            header.localCID = cID;
            Common.FFXIVReplay->savedReplayHeaders[i] = header;
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

        FFXIVReplay.ReplayDataSegment* segment;
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
        if (!Common.IsValid(Common.FFXIVReplay))
            throw new ApplicationException("FFXIVReplay is not initialized!");

        FFXIVReplay.initializeRecording.CreateHook(InitializeRecordingDetour);
        FFXIVReplay.playbackUpdate.CreateHook(PlaybackUpdateDetour);
        FFXIVReplay.requestPlayback.CreateHook(RequestPlaybackDetour);
        FFXIVReplay.beginPlayback.CreateHook(BeginPlaybackDetour);
        FFXIVReplay.getReplayDataSegment.CreateHook(GetReplayDataSegmentDetour);
        FFXIVReplay.onSetChapter.CreateHook(OnSetChapterDetour);
        FFXIVReplay.replayPacket.CreateHook(ReplayPacketDetour);

        SetSavedReplayCIDs(DalamudApi.ClientState.LocalContentId);

        if (Common.FFXIVReplay->InPlayback && Common.FFXIVReplay->fileStream != nint.Zero && *(long*)Common.FFXIVReplay->fileStream == 0)
            LoadReplay(ARealmRecorded.Config.LastLoadedReplay);
    }

    public static void Dispose()
    {
        if (Common.FFXIVReplay != null)
            SetSavedReplayCIDs(0);

        if (loadedReplay == null) return;

        if (Common.FFXIVReplay->InPlayback)
        {
            Common.FFXIVReplay->playbackControls |= 8; // Pause
            DalamudApi.PrintError("Plugin was unloaded, playback will be broken if the plugin or replay is not reloaded.");
        }

        Marshal.FreeHGlobal((nint)loadedReplay);
        loadedReplay = null;
    }
}