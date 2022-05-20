using System;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ARealmRecorded;

public unsafe class Game
{
    private static readonly string replayFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FINAL FANTASY XIV - A Realm Reborn", "replay"); // Path.Combine(Framework.Instance()->UserPath, "replay");
    private static bool replayLoaded;
    private static IntPtr replayBytesPtr;

    private static readonly Memory.Replacer alwaysRecordReplacer = new("24 06 3C 02 75 08 48 8B CB E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer = new("41 FF C6 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84", new byte[] { 0x90, 0x90, 0x90 }, true);

    public static bool quickLoadEnabled = true;
    private static int quickLoadChapter = -1;
    private static int seekingChapter = 0;

    [Signature("76 BA 48 8D 0D", ScanType = ScanType.StaticAddress)]
    public static Structures.FFXIVReplay* ffxivReplay;

    public static bool InPlayback => (ffxivReplay->playbackControls & 4) != 0;

    [Signature("40 53 48 83 EC 20 0F B6 81 12 07 00 00 48 8B D9 A8 04 74 5D")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, void> beginRecording;
    public static void BeginRecording() => beginRecording(ffxivReplay, 1);

    [Signature("E9 ?? ?? ?? ?? 48 83 4B 70 04")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> addRecordingChapter;
    public static bool AddRecordingChapter(byte type) => addRecordingChapter(ffxivReplay, type) != 0;

    [Signature("48 89 5C 24 08 57 48 83 EC 30 F6 81 12 07 00 00 04")] // E8 ?? ?? ?? ?? EB 2B 48 8B CB 89 53 2C (+0x14)
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> requestPlayback;
    public static bool RequestPlayback(byte slot) => requestPlayback(ffxivReplay, slot) != 0;

    private delegate void InitializeRecordingDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 55 57 48 8D 6C 24 B1 48 81 EC 98 00 00 00", DetourName = "InitializeRecordingDetour")]
    private static Hook<InitializeRecordingDelegate> InitializeRecordingHook;
    private static void InitializeRecordingDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        InitializeRecordingHook.Original(ffxivReplay);
        BeginRecording();
    }

    private delegate void BeginPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte canEnter);
    [Signature("E8 ?? ?? ?? ?? 0F B7 17 48 8B CB", DetourName = "BeginPlaybackDetour")]
    private static Hook<BeginPlaybackDelegate> BeginPlaybackHook;
    private static void BeginPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte allowed)
    {
        BeginPlaybackHook.Original(ffxivReplay, allowed);
        if (allowed != 0)
            ReadReplay(ffxivReplay->currentReplaySlot);
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
        return (Structures.FFXIVReplay.ReplayDataSegment*)((long)replayBytesPtr + 0x364 + ffxivReplay->overallDataOffset);
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

    public static void ReadReplay(int slot) => ReadReplay($"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat");

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

    public static void LoadReplayInfo()
    {
        if (!replayLoaded) return;
        ffxivReplay->replayHeader = *(Structures.FFXIVReplay.Header*)replayBytesPtr;
        ffxivReplay->chapters = *(Structures.FFXIVReplay.ChapterArray*)(replayBytesPtr + sizeof(Structures.FFXIVReplay.Header));
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

    // 48 89 5C 24 08 57 48 83 EC 20 33 FF 48 8B D9 89 39 48 89 79 08 ctor
    // E8 ?? ?? ?? ?? 48 8D 8B 48 0B 00 00 E8 ?? ?? ?? ?? 48 8D 8B 38 0B 00 00 dtor
    // E8 ?? ?? ?? ?? EB 10 41 83 78 04 00 EndPlayback
    // 48 89 5C 24 10 55 48 8B EC 48 81 EC 80 00 00 00 48 8B 05 Something to do with loading
    // E8 ?? ?? ?? ?? 84 C0 74 8D SetChapter
    // E8 ?? ?? ?? ?? 3C 40 73 4A GetCurrentChapter?
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
        BeginPlaybackHook.Enable();
        GetReplayDataSegmentHook.Enable();
        OnSetChapterHook.Enable();
        ExecuteCommandHook.Enable();

        if (InPlayback && ffxivReplay->fileStream != IntPtr.Zero && *(long*)ffxivReplay->fileStream == 0)
            ReadReplay(ffxivReplay->currentReplaySlot); //ReadReplay(ARealmRecorded.Config.LastLoadedReplay);
    }

    public static void Dispose()
    {
        InitializeRecordingHook?.Dispose();
        PlaybackUpdateHook?.Dispose();
        BeginPlaybackHook?.Dispose();
        GetReplayDataSegmentHook?.Dispose();
        OnSetChapterHook?.Dispose();
        ExecuteCommandHook?.Dispose();

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