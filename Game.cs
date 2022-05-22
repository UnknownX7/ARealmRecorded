using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ARealmRecorded;

public unsafe class Game
{
    private const int replayHeaderSize = 0x364;
    private static readonly string replayFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FINAL FANTASY XIV - A Realm Reborn", "replay"); // Path.Combine(Framework.Instance()->UserPath, "replay");
    private static bool replayLoaded;
    private static IntPtr replayBytesPtr;
    public static string lastSelectedReplay;
    private static Structures.FFXIVReplay.Header lastSelectedHeader;

    public static bool quickLoadEnabled = true;
    private static int quickLoadChapter = -1;
    private static int seekingChapter = 0;

    private static readonly HashSet<uint> whitelistedContentTypes = new() { 1, 2, 3, 4, 5, 9, 26, 28, 29 }; // 22 Event, 27 Carnivale, 29 Bozja

    private static List<(FileInfo, Structures.FFXIVReplay.Header)> replayList;
    public static List<(FileInfo, Structures.FFXIVReplay.Header)> ReplayList => replayList ?? GetReplayList();

    private static readonly Memory.Replacer alwaysRecordReplacer = new("24 06 3C 02 75 08 48 8B CB E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeRecordReadyToastReplacer = new("BA CB 07 00 00 48 8B CF E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer = new("41 FF C6 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84", new byte[] { 0x90, 0x90, 0x90 }, true);

    [Signature("76 BA 48 8D 0D", ScanType = ScanType.StaticAddress)]
    public static Structures.FFXIVReplay* ffxivReplay;

    public static bool InPlayback => (ffxivReplay->playbackControls & 4) != 0;

    [Signature("40 53 48 83 EC 20 0F B6 81 12 07 00 00 48 8B D9 A8 04 74 5D")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, void> beginRecording;
    public static void BeginRecording() => beginRecording(ffxivReplay, 1);

    [Signature("E9 ?? ?? ?? ?? 48 83 4B 70 04")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> addRecordingChapter;
    public static bool AddRecordingChapter(byte type) => addRecordingChapter(ffxivReplay, type) != 0;

    [Signature("48 89 5C 24 10 57 48 81 EC 70 04 00 00")]
    private static delegate* unmanaged<IntPtr, void> displaySelectedDutyRecording;
    public static void DisplaySelectedDutyRecording(IntPtr agent) => displaySelectedDutyRecording(agent);

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
    }

    private delegate byte RequestPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte slot);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 F6 81 12 07 00 00 04")] // E8 ?? ?? ?? ?? EB 2B 48 8B CB 89 53 2C (+0x14)
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

        if (string.IsNullOrEmpty(lastSelectedReplay))
            ReadReplay(ffxivReplay->currentReplaySlot);
        else
            ReadReplay(lastSelectedReplay);
    }

    [Signature("E8 ?? ?? ?? ?? F6 83 12 07 00 00 04", DetourName = "PlaybackUpdateDetour")]
    private static Hook<InitializeRecordingDelegate> PlaybackUpdateHook;
    private static void PlaybackUpdateDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        PlaybackUpdateHook.Original(ffxivReplay);

        if ((ffxivReplay->playbackControls & 4) == 0)
        {
            if (ffxivReplay->chapters[0]->type == 1) // For some reason the barrier dropping in dungeons is 5, but in trials it's 1
                ffxivReplay->chapters[0]->type = 5;
            return;
        }

        if (!replayLoaded) return;

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
        if (!replayLoaded)
            return GetReplayDataSegmentHook.Original(ffxivReplay);
        if (ffxivReplay->overallDataOffset >= ffxivReplay->replayHeader.replayLength)
            return null;
        return (Structures.FFXIVReplay.ReplayDataSegment*)((long)replayBytesPtr + replayHeaderSize + ffxivReplay->overallDataOffset);
    }

    private delegate void OnSetChapterDelegate(Structures.FFXIVReplay* ffxivReplay, byte chapter);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 48 8B D9 0F B6 FA 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 24", DetourName = "OnSetChapterDetour")]
    private static Hook<OnSetChapterDelegate> OnSetChapterHook;
    private static void OnSetChapterDetour(Structures.FFXIVReplay* ffxivReplay, byte chapter)
    {
        OnSetChapterHook.Original(ffxivReplay, chapter);

        if (!quickLoadEnabled || chapter <= 0 || ffxivReplay->chapters.length < 2) return;

        quickLoadChapter = chapter;
        seekingChapter = -1;
        DoQuickLoad();
    }

    private delegate void ExecuteCommandDelegate(int a1, int a2, int a3, int a4, int a5);
    [Signature("E8 ?? ?? ?? ?? 8D 43 0A")]
    private static Hook<ExecuteCommandDelegate> ExecuteCommandHook;
    private static void ExecuteCommandDetour(int a1, int a2, int a3, int a4, int a5)
    {
        if (a1 == 315 && InPlayback) return; // Block GPose and Idle Camera from sending packets
        ExecuteCommandHook.Original(a1, a2, a3, a4, a5);
    }

    private delegate byte DisplayRecordingOnDTRBarDelegate(IntPtr agent);
    [Signature("E8 ?? ?? ?? ?? 44 0F B6 C0 BA 4F 00 00 00")]
    private static Hook<DisplayRecordingOnDTRBarDelegate> DisplayRecordingOnDTRBarHook;
    private static byte DisplayRecordingOnDTRBarDetour(IntPtr agent)
    {
        if (DisplayRecordingOnDTRBarHook.Original(agent) != 0)
            return 1;
        return (byte)((DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi ? 1 : 0) & ffxivReplay->status >> 2);
    }

    public static string GetReplaySlotName(int slot) => $"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat";

    public static void ReadReplay(int slot) => ReadReplay(GetReplaySlotName(slot));

    public static void ReadReplay(string replayName)
    {
        if (replayLoaded)
            Marshal.FreeHGlobal(replayBytesPtr);

        try
        {
            var file = new FileInfo(Path.Combine(replayFolder, replayName));
            using var fs = File.OpenRead(file.FullName);
            replayBytesPtr = Marshal.AllocHGlobal((int)fs.Length);
            _ = fs.Read(new Span<byte>((void*)replayBytesPtr, (int)fs.Length));
            replayLoaded = true;
            ARealmRecorded.Config.LastLoadedReplay = replayName;

            if (ffxivReplay->dataLoadType != 7) return;

            LoadReplayInfo();
            ffxivReplay->dataLoadType = 0;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay {replayName}\n{e}");
            replayLoaded = false;
        }
    }

    public static Structures.FFXIVReplay.Header? ReadReplayHeader(string replayName)
    {
        try
        {
            var file = new FileInfo(Path.Combine(replayFolder, replayName));
            using var fs = File.OpenRead(file.FullName);
            var bytes = new byte[replayHeaderSize];
            if (fs.Read(bytes, 0, replayHeaderSize) != replayHeaderSize)
                return null;
            fixed (byte* ptr = &bytes[0])
            {
                return *(Structures.FFXIVReplay.Header*)ptr;
            }
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay header {replayName}\n{e}");
            return null;
        }
    }

    public static void LoadReplayInfo()
    {
        if (!replayLoaded) return;
        ffxivReplay->replayHeader = *(Structures.FFXIVReplay.Header*)replayBytesPtr;
        ffxivReplay->chapters = *(Structures.FFXIVReplay.ChapterArray*)(replayBytesPtr + sizeof(Structures.FFXIVReplay.Header));
    }

    public static void FixNextReplaySaveSlot()
    {
        if (!ffxivReplay->savedReplayHeaders[ffxivReplay->nextReplaySaveSlot].IsLocked) return;

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
        {
            if (ffxivReplay->chapters[i]->type == type) return i;
        }
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

    public static void JumpToChapter(byte chapter)
    {
        var jumpChapter = ffxivReplay->chapters[chapter];
        ffxivReplay->overallDataOffset = jumpChapter->offset;
        ffxivReplay->seek = jumpChapter->ms / 1000f;
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
        return ((delegate* unmanaged<UIModule*, byte>)uiModule->vfunc[71])(uiModule) != 0;
    }

    public static bool EnterIdleCamera()
    {
        var uiModule = Framework.Instance()->GetUiModule();
        var focus = DalamudApi.TargetManager.FocusTarget;
        return ((delegate* unmanaged<UIModule*, byte, long, byte>)uiModule->vfunc[74])(uiModule, 0, focus != null ? focus.ObjectId : 0xE0000000) != 0;
    }

    public static List<(FileInfo, Structures.FFXIVReplay.Header)> GetReplayList()
    {
        try
        {
            var directory = new DirectoryInfo(replayFolder);
            var list = (from file in directory.GetFiles()
                    where file.Extension == ".dat"
                    let header = ReadReplayHeader(file.Name)
                    where header is { IsValid: true }
                    select (file, header.Value)
                ).ToList();
            replayList = list;
        }
        catch
        {
            replayList = new();
        }

        return replayList;
    }

    public static void SetDutyRecorderMenuSelection(IntPtr agent, byte slot)
    {
        *(byte*)(agent + 0x2C) = slot;
        *(byte*)(agent + 0x2A) = 1;
        DisplaySelectedDutyRecording(agent);
    }

    public static void SetDutyRecorderMenuSelection(IntPtr agent, string fileName, Structures.FFXIVReplay.Header header)
    {
        header.localCID = DalamudApi.ClientState.LocalContentId;
        lastSelectedReplay = fileName;
        lastSelectedHeader = header;
        var prevHeader = ffxivReplay->savedReplayHeaders[0];
        ffxivReplay->savedReplayHeaders[0] = header;
        SetDutyRecorderMenuSelection(agent, 0);
        ffxivReplay->savedReplayHeaders[0] = prevHeader;
        *(byte*)(agent + 0x2C) = 100;
    }

    public static void CopyRecordingIntoSlot(IntPtr agent, FileInfo file, Structures.FFXIVReplay.Header header, byte slot)
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

    // 48 89 5C 24 08 57 48 83 EC 20 33 FF 48 8B D9 89 39 48 89 79 08 ctor
    // E8 ?? ?? ?? ?? 48 8D 8B 48 0B 00 00 E8 ?? ?? ?? ?? 48 8D 8B 38 0B 00 00 dtor
    // 40 53 48 83 EC 20 80 A1 12 07 00 00 F3 Initialize
    // 40 53 48 83 EC 20 0F B6 81 13 07 00 00 48 8B D9 A8 04 Update
    // 48 83 EC 38 0F B6 91 13 07 00 00 RequestEndPlayback
    // E8 ?? ?? ?? ?? EB 10 41 83 78 04 00 EndPlayback
    // 48 89 5C 24 10 55 48 8B EC 48 81 EC 80 00 00 00 48 8B 05 Something to do with loading
    // E8 ?? ?? ?? ?? 84 C0 74 8D SetChapter
    // E8 ?? ?? ?? ?? 3C 40 73 4A GetCurrentChapter
    // 40 53 48 83 EC 20 0F B6 81 13 07 00 00 48 8B D9 24 06 ResetPlayback
    // F6 81 13 07 00 00 04 74 11 SetTimescale (No longer used by anything)
    // 40 53 48 83 EC 20 F3 0F 10 81 00 07 00 00 48 8B D9 SetSoundTimescale1? Doesn't seem to work (Last function)
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

        if (InPlayback && ffxivReplay->fileStream != IntPtr.Zero && *(long*)ffxivReplay->fileStream == 0)
            ReadReplay(ARealmRecorded.Config.LastLoadedReplay);
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

        if (!replayLoaded) return;

        if (InPlayback)
        {
            ffxivReplay->playbackControls |= 8; // Pause
            ARealmRecorded.PrintError("Plugin was unloaded, playback will be broken if the plugin or recording is not reloaded.");
        }

        Marshal.FreeHGlobal(replayBytesPtr);
        replayLoaded = false;
    }
}