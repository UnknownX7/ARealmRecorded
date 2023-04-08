using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Memory;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace ARealmRecorded;

public unsafe class Game
{
    private static readonly string replayFolder = Path.Combine(Framework.Instance()->UserPath, "replay");
    private static readonly string autoRenamedFolder = Path.Combine(replayFolder, "autorenamed");
    private static readonly string deletedFolder = Path.Combine(replayFolder, "deleted");
    private static Structures.FFXIVReplay.ReplayFile* loadedReplay = null;
    public static string lastSelectedReplay;
    private static Structures.FFXIVReplay.Header lastSelectedHeader;

    private static int quickLoadChapter = -1;
    private static int seekingChapter = 0;
    private static uint seekingOffset = 0;

    private static int currentRecordingSlot = -1;
    private static readonly Regex bannedFolderCharacters = new("[\\\\\\/:\\*\\?\"\\<\\>\\|\u0000-\u001F]");

    private static readonly HashSet<uint> whitelistedContentTypes = new() { 1, 2, 3, 4, 5, 9, 28, 29, 30 }; // 22 Event, 26 Eureka, 27 Carnivale

    private static List<(FileInfo, Structures.FFXIVReplay.Header)> replayList;
    public static List<(FileInfo, Structures.FFXIVReplay.Header)> ReplayList => replayList ?? GetReplayList();
    
    private const int RsfSize = 0x48;
    private const ushort RsfOpcde = 0xF002;
    private static List<byte[]> RsfBuffer = new();
    private const ushort RsvOpcde = 0xF001;
    private static List<byte[]> RsvBuffer = new();

    private static readonly Memory.Replacer alwaysRecordReplacer = new("A8 04 75 27 A8 02 74 23 48 8B", new byte[] { 0xEB, 0x21 }, true); // 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90
    private static readonly Memory.Replacer removeRecordReadyToastReplacer = new("BA CB 07 00 00 48 8B CF E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer = new("41 FF C6 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84", new byte[] { 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer2 = new("77 57 48 8B 0D ?? ?? ?? ?? 33 C0", new byte[] { 0x90, 0x90 }, true);
    private static readonly Memory.Replacer forceFastForwardReplacer = new("0F 83 ?? ?? ?? ?? 0F B7 47 02 4C 8D 47 0C", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });

    [Signature("48 8D 0D ?? ?? ?? ?? 88 44 24 24", ScanType = ScanType.StaticAddress)]
    public static Structures.FFXIVReplay* ffxivReplay;

    [Signature("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 0E", ScanType = ScanType.StaticAddress)]
    private static byte* waymarkToggle; // Actually a uint, but only seems to use the first 2 bits

    public static bool InPlayback => (ffxivReplay->playbackControls & 4) != 0;
    public static bool IsRecording => (ffxivReplay->status & 0x74) == 0x74;
    public static bool IsLoadingChapter => ffxivReplay->selectedChapter < 0x40;

    public static bool IsWaymarkVisible => (*waymarkToggle & 2) == 0;

    [Signature("?? ?? 00 00 01 75 74 85 FF 75 07 E8")]
    public static short contentDirectorOffset;

    [Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 74 5D")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, void> beginRecording;
    public static void BeginRecording() => beginRecording(ffxivReplay, 1);

    [Signature("E8 ?? ?? ?? ?? 84 C0 74 8D 48 8B CE")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> setChapter;
    private static byte SetChapter(byte chapter) => setChapter(ffxivReplay, chapter);

    //[Signature("E9 ?? ?? ?? ?? 48 83 4B 70 04")]
    //private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> addRecordingChapter;
    //public static bool AddRecordingChapter(byte type) => addRecordingChapter(ffxivReplay, type) != 0;

    //[Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 24 06 3C 04 75 5D 83 B9")]
    //private static delegate* unmanaged<Structures.FFXIVReplay*, void> resetPlayback;
    //public static void ResetPlayback() => resetPlayback(ffxivReplay);

    [Signature("48 89 5C 24 10 57 48 81 EC 70 04 00 00")]
    private static delegate* unmanaged<nint, void> displaySelectedDutyRecording;
    public static void DisplaySelectedDutyRecording(nint agent) => displaySelectedDutyRecording(agent);

    private delegate void InitializeRecordingDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 55 57 48 8D 6C 24 B1 48 81 EC 98 00 00 00", DetourName = "InitializeRecordingDetour")]
    private static Hook<InitializeRecordingDelegate> InitializeRecordingHook;
    private static void InitializeRecordingDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        var id = ffxivReplay->initZonePacket.contentFinderCondition;
        if (id == 0) return;

        var contentFinderCondition = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.ContentFinderCondition>()?.GetRow(id);
        if (contentFinderCondition == null) return;

        var contentType = contentFinderCondition.ContentType.Row;
        if (!whitelistedContentTypes.Contains(contentType)) return;

        FixNextReplaySaveSlot();
        InitializeRecordingHook.Original(ffxivReplay);
        BeginRecording();

        var header = ffxivReplay->replayHeader;
        header.localCID = 0;
        ffxivReplay->replayHeader = header;

        if (contentDirectorOffset > 0)
            ContentDirectorTimerUpdateHook?.Enable();

        if (IsRecording) {
            PluginLog.Debug($"Start recording {RsfBuffer.Count} rsf");
            foreach (var rsf in RsfBuffer) {
                fixed (byte* data = rsf) {
                    //var size = *(int*)data;   //Value size
                    //var length = size + 0x4 + 0x30;     //package size
                    RecordPacket(ffxivReplay, 0xE000_0000, RsfOpcde, (IntPtr)data, (ulong)rsf.Length);
                }
            }
            PluginLog.Debug($"Start recording {RsvBuffer.Count} rsv");
            foreach (var rsv in RsvBuffer) {
                fixed (byte* data = rsv) {
                    RecordPacket(ffxivReplay, 0xE000_0000, RsvOpcde, (IntPtr)data, (ulong)rsv.Length);
                }
            }
            RsfBuffer.Clear();
            RsvBuffer.Clear();
        }
    }

    private delegate byte RequestPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte slot);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 F6 81 ?? ?? ?? ?? 04", DetourName = "RequestPlaybackDetour")] // E8 ?? ?? ?? ?? EB 2B 48 8B CB 89 53 2C (+0x14)
    private static Hook<RequestPlaybackDelegate> RequestPlaybackHook;
    public static byte RequestPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte slot)
    {
        var customSlot = slot == 100;
        Structures.FFXIVReplay.Header prevHeader = new();

        if (customSlot)
        {
            slot = 0;
            prevHeader = ffxivReplay->savedReplayHeaders[0];
            ffxivReplay->savedReplayHeaders[0] = lastSelectedHeader;
        }
        else
        {
            lastSelectedReplay = null;
        }

        var ret = RequestPlaybackHook.Original(ffxivReplay, slot);

        if (customSlot)
            ffxivReplay->savedReplayHeaders[0] = prevHeader;

        return ret;
    }

    private delegate void BeginPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte canEnter);
    [Signature("E8 ?? ?? ?? ?? 0F B7 17 48 8B CB", DetourName = "BeginPlaybackDetour")]
    private static Hook<BeginPlaybackDelegate> BeginPlaybackHook;
    private static void BeginPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte allowed)
    {
        BeginPlaybackHook.Original(ffxivReplay, allowed);
        if (allowed == 0) return;

        UnloadReplay();

        if (string.IsNullOrEmpty(lastSelectedReplay))
            LoadReplay(ffxivReplay->currentReplaySlot);
        else
            LoadReplay(lastSelectedReplay);
    }

    [Signature("E8 ?? ?? ?? ?? F6 83 ?? ?? ?? ?? 04 74 38 F6 83 ?? ?? ?? ?? 01", DetourName = "PlaybackUpdateDetour")]
    private static Hook<InitializeRecordingDelegate> PlaybackUpdateHook;
    private static void PlaybackUpdateDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        PlaybackUpdateHook.Original(ffxivReplay);

        UpdateAutoRename();

        if (IsRecording && ffxivReplay->chapters[0]->type == 1) // For some reason the barrier dropping in dungeons is 5, but in trials it's 1
            ffxivReplay->chapters[0]->type = 5;

        if (!InPlayback) return;

        SetConditionFlag(ConditionFlag.OccupiedInCutSceneEvent, false);

        if (loadedReplay == null) return;

        ffxivReplay->dataLoadType = 0;
        ffxivReplay->dataOffset = 0;

        if (quickLoadChapter < 2) return;

        var seekedTime = ffxivReplay->chapters[seekingChapter]->ms / 1000f;
        if (seekedTime > ffxivReplay->seek) return;

        DoQuickLoad();
    }

    private delegate Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 53 48 83 EC 20 8B 81 90 00 00 00")]
    private static Hook<GetReplayDataSegmentDelegate> GetReplayDataSegmentHook;
    public static Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        // Needs to be here to prevent infinite looping
        if (seekingOffset > 0 && seekingOffset <= ffxivReplay->overallDataOffset)
        {
            forceFastForwardReplacer.Disable();
            seekingOffset = 0;
        }

        // Absurdly hacky, but it works
        if (!ARealmRecorded.Config.EnableQuickLoad || ARealmRecorded.Config.MaxSeekDelta <= 100 || ffxivReplay->seekDelta >= ARealmRecorded.Config.MaxSeekDelta)
            removeProcessingLimitReplacer2.Disable();
        else
            removeProcessingLimitReplacer2.Enable();

        return loadedReplay == null ? GetReplayDataSegmentHook.Original(ffxivReplay) : loadedReplay->GetDataSegment((uint)ffxivReplay->overallDataOffset);
    }

    private delegate void OnSetChapterDelegate(Structures.FFXIVReplay* ffxivReplay, byte chapter);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 48 8B D9 0F B6 FA 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 24", DetourName = "OnSetChapterDetour")]
    private static Hook<OnSetChapterDelegate> OnSetChapterHook;
    private static void OnSetChapterDetour(Structures.FFXIVReplay* ffxivReplay, byte chapter)
    {
        OnSetChapterHook.Original(ffxivReplay, chapter);

        if (!ARealmRecorded.Config.EnableQuickLoad || chapter <= 0 || ffxivReplay->chapters.length < 2) return;

        quickLoadChapter = chapter;
        seekingChapter = -1;
        DoQuickLoad();
    }

    private delegate byte ExecuteCommandDelegate(uint clientTrigger, int param1, int param2, int param3, int param4);
    [Signature("E8 ?? ?? ?? ?? 8D 43 0A")]
    private static Hook<ExecuteCommandDelegate> ExecuteCommandHook;
    private static byte ExecuteCommandDetour(uint clientTrigger, int param1, int param2, int param3, int param4)
    {
        if (!InPlayback || clientTrigger is 201 or 1981) return ExecuteCommandHook.Original(clientTrigger, param1, param2, param3, param4); // Block GPose and Idle Camera from sending packets
        if (clientTrigger == 314) // Mimic GPose and Idle Camera ConditionFlag for plugin compatibility
            SetConditionFlag(ConditionFlag.WatchingCutscene, param1 != 0);
        return 0;
    }

    private delegate byte DisplayRecordingOnDTRBarDelegate(nint agent);
    [Signature("E8 ?? ?? ?? ?? 44 0F B6 C0 BA 4F 00 00 00")]
    private static Hook<DisplayRecordingOnDTRBarDelegate> DisplayRecordingOnDTRBarHook;
    private static byte DisplayRecordingOnDTRBarDetour(nint agent) => (byte)(DisplayRecordingOnDTRBarHook.Original(agent) != 0
        || ARealmRecorded.Config.EnableRecordingIcon && IsRecording && DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi ? 1 : 0);

    private delegate void ContentDirectorTimerUpdateDelegate(nint contentDirector);
    [Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 0F 84 ?? ?? ?? ?? A8 08", DetourName = "ContentDirectorTimerUpdateDetour")]
    private static Hook<ContentDirectorTimerUpdateDelegate> ContentDirectorTimerUpdateHook;
    private static void ContentDirectorTimerUpdateDetour(nint contentDirector)
    {
        if ((*(byte*)(contentDirector + contentDirectorOffset) & 12) == 12)
        {
            ffxivReplay->status |= 64;
            ContentDirectorTimerUpdateHook.Disable();
        }

        ContentDirectorTimerUpdateHook.Original(contentDirector);
    }

    private delegate nint EventBeginDelegate(nint a1, nint a2);
    [Signature("40 55 53 57 41 55 41 57 48 8D 6C 24 C9")]
    private static Hook<EventBeginDelegate> EventBeginHook;
    private static nint EventBeginDetour(nint a1, nint a2) => !InPlayback || ConfigModule.Instance()->GetIntValue(ConfigOption.CutsceneSkipIsContents) == 0 ? EventBeginHook.Original(a1, a2) : nint.Zero;

    public unsafe delegate long RsvReceiveDelegate(IntPtr a1);
    [Signature("44 8B 09 4C 8D 41 34", DetourName = nameof(RsvReceiveDetour))]
    private static Hook<RsvReceiveDelegate> RsvReceiveHook;
    private static long RsvReceiveDetour(IntPtr a1)
    {
        //PluginLog.Debug("Received a RSV packet,");
        var size = *(int*)a1;   //Value size
        var length = size + 0x4 + 0x30;     //package size
        RsvBuffer.Add(MemoryHelper.ReadRaw(a1, length));
        var ret = RsvReceiveHook.Original(a1);
        //PluginLog.Debug($"RSV:RET = {ret:X},Num of received:{RsvBuffer.Count}");
        return ret;
    }

    public unsafe delegate long RsfReceiveDelegate(IntPtr a1);
    [Signature("48 8B 11 4C 8D 41 08", DetourName = nameof(RsfReceiveDetour))]
    private static Hook<RsfReceiveDelegate> RsfReceiveHook;
    private static long RsfReceiveDetour(IntPtr a1)
    {
        //PluginLog.Debug("Received a RSF packet");
        RsfBuffer.Add(MemoryHelper.ReadRaw(a1, RsfSize));
        var ret = RsfReceiveHook.Original(a1);
        //PluginLog.Debug($"RSF:RET = {ret:X},Num of received:{RsvBuffer.Count}");
        return ret;
    }
    [Signature("E8 ?? ?? ?? ?? 84 C0 74 60 33 C0")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, uint, ushort, IntPtr, ulong, uint> recordPacket;
    public static void RecordPacket(Structures.FFXIVReplay* replayModule, uint targetId, ushort opcode, IntPtr data, ulong length) => recordPacket(replayModule,targetId,opcode,data,length);

    private unsafe delegate uint DispatchPacketDelegate(Structures.FFXIVReplay* replayModule, IntPtr header, IntPtr data);
    [Signature("E8 ?? ?? ?? ?? 80 BB ?? ?? ?? ?? ?? 77 93", DetourName = nameof(DispatchPacketDetour))]
    private static Hook<DispatchPacketDelegate> DispatchPacketHook;
    private static unsafe uint DispatchPacketDetour(Structures.FFXIVReplay* replayModule, nint header, nint data)
    {
        var opcode = *(ushort*)header;
        //PluginLog.Debug($"Dispatch:0x{opcode:X}");
        switch (opcode) {
            case RsvOpcde:
                RsvReceiveHook.Original(data);
                break;
            case RsfOpcde:
                RsfReceiveHook.Original(data);
                break;
        }
        return DispatchPacketHook.Original(replayModule, header, data);
    }

    public static string GetReplaySlotName(int slot) => $"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat";

    private static void UpdateAutoRename()
    {
        switch (IsRecording)
        {
            case true when currentRecordingSlot < 0:
                currentRecordingSlot = ffxivReplay->nextReplaySaveSlot;
                break;
            case false when currentRecordingSlot >= 0:
                AutoRenameRecording();
                currentRecordingSlot = -1;
                SetSavedRecordingCIDs(DalamudApi.ClientState.LocalContentId);
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
        ffxivReplay->replayHeader = loadedReplay->header;
        ffxivReplay->chapters = loadedReplay->chapters;
        ffxivReplay->dataLoadType = 0;

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

    public static Structures.FFXIVReplay.ReplayFile* ReadReplay(string path)
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
            PluginLog.Error($"Failed to read replay {path}\n{e}");

            if (allocated)
            {
                Marshal.FreeHGlobal(ptr);
                ptr = nint.Zero;
            }
        }

        return (Structures.FFXIVReplay.ReplayFile*)ptr;
    }

    public static Structures.FFXIVReplay.Header? ReadReplayHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var size = sizeof(Structures.FFXIVReplay.Header);
            var bytes = new byte[size];
            if (fs.Read(bytes, 0, size) != size)
                return null;
            fixed (byte* ptr = &bytes[0])
                return *(Structures.FFXIVReplay.Header*)ptr;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay header {path}\n{e}");
            return null;
        }
    }

    public static void FixNextReplaySaveSlot()
    {
        for (byte i = 0; i < 3; i++)
        {
            if (i != 2)
            {
                var header = ffxivReplay->savedReplayHeaders[i];
                if (header.IsLocked) continue;
            }

            ffxivReplay->nextReplaySaveSlot = i;
            return;
        }
    }

    public static byte FindNextChapterType(byte startChapter, byte type)
    {
        for (byte i = (byte)(startChapter + 1); i < ffxivReplay->chapters.length; i++)
            if (ffxivReplay->chapters[i]->type == type) return i;
        return 0;
    }

    public static byte GetPreviousStartChapter(byte chapter)
    {
        var foundPreviousStart = false;
        for (byte i = chapter; i > 0; i--)
        {
            if (ffxivReplay->chapters[i]->type != 2) continue;

            if (foundPreviousStart)
                return i;
            foundPreviousStart = true;
        }
        return 0;
    }

    public static byte FindPreviousChapterFromTime(uint ms)
    {
        for (byte i = (byte)(ffxivReplay->chapters.length - 1); i > 0; i--)
            if (ffxivReplay->chapters[i]->ms <= ms) return i;
        return 0;
    }

    public static Structures.FFXIVReplay.ReplayDataSegment* FindNextDataSegment(uint ms, out uint offset)
    {
        offset = 0;

        Structures.FFXIVReplay.ReplayDataSegment* segment;
        while ((segment = loadedReplay->GetDataSegment(offset)) != null)
        {
            if (segment->ms >= ms) return segment;
            offset += segment->Length;
        }

        return null;
    }

    public static void JumpToChapter(byte chapter)
    {
        var jumpChapter = ffxivReplay->chapters[chapter];
        if (jumpChapter == null) return;
        ffxivReplay->overallDataOffset = jumpChapter->offset;
        ffxivReplay->seek = jumpChapter->ms / 1000f;
    }

    public static void JumpToTime(uint ms)
    {
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;
        ffxivReplay->overallDataOffset = offset;
        ffxivReplay->seek = segment->ms / 1000f;
    }

    public static void JumpToTimeBeforeChapter(byte chapter, uint ms)
    {
        var jumpChapter = ffxivReplay->chapters[chapter];
        if (jumpChapter == null) return;
        JumpToTime(jumpChapter->ms > ms ? jumpChapter->ms - ms : 0);
    }

    public static void SeekToTime(uint ms)
    {
        if (IsLoadingChapter) return;

        var prevChapter = FindPreviousChapterFromTime(ms);
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;

        seekingOffset = offset;
        forceFastForwardReplacer.Enable();
        SetChapter(prevChapter);
    }

    public static void ReplaySection(byte from, byte to)
    {
        if (from != 0 && ffxivReplay->overallDataOffset < ffxivReplay->chapters[from]->offset)
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

    public static bool EnterGroupPose()
    {
        var uiModule = Framework.Instance()->GetUiModule();
        return ((delegate* unmanaged<UIModule*, byte>)uiModule->vfunc[75])(uiModule) != 0; // 48 89 5C 24 08 57 48 83 EC 20 48 8B F9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B C8
    }

    public static bool EnterIdleCamera()
    {
        var uiModule = Framework.Instance()->GetUiModule();
        var focus = DalamudApi.TargetManager.FocusTarget;
        return ((delegate* unmanaged<UIModule*, byte, long, byte>)uiModule->vfunc[78])(uiModule, 0, focus != null ? focus.ObjectId : 0xE0000000) != 0; // 48 89 5C 24 08 57 48 83 EC 20 48 8B 01 49 8B D8 0F B6 FA
    }

    public static List<(FileInfo, Structures.FFXIVReplay.Header)> GetReplayList()
    {
        try
        {
            var directory = new DirectoryInfo(replayFolder);

            var renamedDirectory = new DirectoryInfo(autoRenamedFolder);
            if (!renamedDirectory.Exists)
                renamedDirectory.Create();

            var list = (from file in directory.GetFiles().Concat(renamedDirectory.GetFiles())
                    where file.Extension == ".dat"
                    let header = ReadReplayHeader(file.FullName)
                    where header is { IsValid: true }
                    select (file, header.Value)
                ).OrderByDescending(t => t.Value.IsPlayable).ThenByDescending(t => t.file.CreationTime).ToList();

            replayList = list;
        }
        catch
        {
            replayList = new();
        }

        return replayList;
    }

    public static void RenameRecording(FileInfo file, string name)
    {
        try
        {
            file.MoveTo(Path.Combine(replayFolder, $"{name}.dat"));
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to rename recording\n{e}");
        }
    }

    public static void AutoRenameRecording()
    {
        try
        {
            var fileName = GetReplaySlotName(currentRecordingSlot);
            var (file, _) = GetReplayList().First(t => t.Item1.Name == fileName);

            var name = $"{bannedFolderCharacters.Replace(ffxivReplay->contentTitle.ToString(), string.Empty)} {DateTime.Now:yyyy.MM.dd HH.mm.ss}";
            file.MoveTo(Path.Combine(autoRenamedFolder, $"{name}.dat"));

            var renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            if (renamedFiles.Count > 30)
                renamedFiles.OrderBy(f => f.CreationTime).First().Delete();

            GetReplayList();
            ffxivReplay->savedReplayHeaders[currentRecordingSlot] = new Structures.FFXIVReplay.Header();
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to rename recording\n{e}");
        }
    }

    public static void DeleteRecording(FileInfo file)
    {
        try
        {
            var deletedDirectory = new DirectoryInfo(deletedFolder);
            if (!deletedDirectory.Exists)
                deletedDirectory.Create();

            file.MoveTo(Path.Combine(deletedFolder, file.Name));

            var deletedFiles = deletedDirectory.GetFiles().Where(f => f.Extension == ".dat").ToList();
            if (deletedFiles.Count > 10)
                deletedFiles.OrderBy(f => f.CreationTime).First().Delete();

            GetReplayList();
            ARealmRecorded.PrintEcho("Successfully moved the recording to the deleted folder!");
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to delete recording\n{e}");
        }
    }

    public static void SetDutyRecorderMenuSelection(nint agent, byte slot)
    {
        *(byte*)(agent + 0x2C) = slot;
        *(byte*)(agent + 0x2A) = 1;
        DisplaySelectedDutyRecording(agent);
    }

    public static void SetDutyRecorderMenuSelection(nint agent, string path, Structures.FFXIVReplay.Header header)
    {
        header.localCID = DalamudApi.ClientState.LocalContentId;
        lastSelectedReplay = path;
        lastSelectedHeader = header;
        var prevHeader = ffxivReplay->savedReplayHeaders[0];
        ffxivReplay->savedReplayHeaders[0] = header;
        SetDutyRecorderMenuSelection(agent, 0);
        ffxivReplay->savedReplayHeaders[0] = prevHeader;
        *(byte*)(agent + 0x2C) = 100;
    }

    public static void CopyRecordingIntoSlot(nint agent, FileInfo file, Structures.FFXIVReplay.Header header, byte slot)
    {
        if (slot > 2) return;
        try
        {
            file.CopyTo(Path.Combine(file.DirectoryName!, GetReplaySlotName(slot)), true);
            ffxivReplay->savedReplayHeaders[slot] = header;
            SetDutyRecorderMenuSelection(agent, slot);
            GetReplayList();
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to copy recording to slot {slot + 1}\n{e}");
        }
    }

    public static void SetSavedRecordingCIDs(ulong cID)
    {
        if (ffxivReplay->savedReplayHeaders == null) return;

        for (int i = 0; i < 3; i++)
        {
            var header = ffxivReplay->savedReplayHeaders[i];
            if (!header.IsValid) continue;
            header.localCID = cID;
            ffxivReplay->savedReplayHeaders[i] = header;
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

    public static void ToggleWaymarks()
    {
        if ((*waymarkToggle & 2) != 0)
            *waymarkToggle -= 2;
        else
            *waymarkToggle += 2;
    }

    public static void SetConditionFlag(ConditionFlag flag, bool b) => *(bool*)(DalamudApi.Condition.Address + (int)flag) = b;

#if DEBUG
    public static void ReadPackets(string path)
    {
        var replay = ReadReplay(path);
        if (replay == null) return;

        var opcodeCount = new Dictionary<uint, uint>();
        var opcodeLengths = new Dictionary<uint, uint>();

        var offset = 0u;
        var totalPackets = 0u;

        Structures.FFXIVReplay.ReplayDataSegment* segment;
        while ((segment = replay->GetDataSegment(offset)) != null)
        {
            opcodeCount.TryGetValue(segment->opcode, out var count);
            opcodeCount[segment->opcode] = ++count;

            opcodeLengths[segment->opcode] = segment->dataLength;
            offset += segment->Length;
            ++totalPackets;
        }

        Marshal.FreeHGlobal((nint)replay);

        PluginLog.Information("-------------------");
        PluginLog.Information($"Opcodes inside: {path} (Total: [{opcodeCount.Count}] {totalPackets})");
        foreach (var (opcode, count) in opcodeCount)
            PluginLog.Information($"[{opcode:X}] {count} ({opcodeLengths[opcode]})");
        PluginLog.Information("-------------------");
    }
#endif

    // 48 89 5C 24 08 57 48 83 EC 20 33 FF 48 8B D9 89 39 48 89 79 08 ctor
    // E8 ?? ?? ?? ?? 48 8D 8B 48 0B 00 00 E8 ?? ?? ?? ?? 48 8D 8B 38 0B 00 00 dtor
    // 40 53 48 83 EC 20 80 A1 ?? ?? ?? ?? F3 Initialize
    // 40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 75 09 Update
    // 48 83 EC 38 0F B6 91 ?? ?? ?? ?? 0F B6 C2 RequestEndPlayback
    // E8 ?? ?? ?? ?? EB 10 41 83 78 04 00 EndPlayback
    // 48 89 5C 24 10 55 48 8B EC 48 81 EC 80 00 00 00 48 8B 05 Something to do with loading
    // E8 ?? ?? ?? ?? 3C 40 73 4A GetCurrentChapter
    // F6 81 ?? ?? ?? ?? 04 74 11 SetTimescale (No longer used by anything)
    // 40 53 48 83 EC 20 F3 0F 10 81 ?? ?? ?? ?? 48 8B D9 F3 0F 10 0D SetSoundTimescale1? Doesn't seem to work (Last function)
    // E8 ?? ?? ?? ?? 44 0F B6 D8 C7 03 02 00 00 00 Function handling the UI buttons

    public static void Initialize()
    {
        // TODO change back to static whenever support is added
        //SignatureHelper.Initialise(typeof(Game));
        SignatureHelper.Initialise(new Game());
        InitializeRecordingHook.Enable();
        PlaybackUpdateHook.Enable();
        RequestPlaybackHook.Enable();
        BeginPlaybackHook.Enable();
        GetReplayDataSegmentHook.Enable();
        OnSetChapterHook.Enable();
        ExecuteCommandHook.Enable();
        DisplayRecordingOnDTRBarHook.Enable();
        EventBeginHook.Enable();
        RsvReceiveHook.Enable();
        RsfReceiveHook.Enable();
        DispatchPacketHook.Enable();

        waymarkToggle += 0x48;

        SetSavedRecordingCIDs(DalamudApi.ClientState.LocalContentId);

        if (InPlayback && ffxivReplay->fileStream != nint.Zero && *(long*)ffxivReplay->fileStream == 0)
            LoadReplay(ARealmRecorded.Config.LastLoadedReplay);
    }

    public static void Dispose()
    {
        InitializeRecordingHook?.Dispose();
        PlaybackUpdateHook?.Dispose();
        RequestPlaybackHook?.Dispose();
        BeginPlaybackHook?.Dispose();
        GetReplayDataSegmentHook?.Dispose();
        OnSetChapterHook?.Dispose();
        ExecuteCommandHook?.Dispose();
        DisplayRecordingOnDTRBarHook?.Dispose();
        ContentDirectorTimerUpdateHook?.Dispose();
        EventBeginHook?.Dispose();
        RsvReceiveHook?.Dispose();
        RsfReceiveHook?.Dispose();
        DispatchPacketHook?.Dispose();

        if (ffxivReplay != null)
            SetSavedRecordingCIDs(0);

        if (loadedReplay == null) return;

        if (InPlayback)
        {
            ffxivReplay->playbackControls |= 8; // Pause
            ARealmRecorded.PrintError("Plugin was unloaded, playback will be broken if the plugin or recording is not reloaded.");
        }

        Marshal.FreeHGlobal((nint)loadedReplay);
        loadedReplay = null;
    }
}