using System;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Utility.Signatures;

namespace ARealmRecorded;

public unsafe class Game
{
    private static readonly string replayFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FINAL FANTASY XIV - A Realm Reborn", "replay");
    private static bool replayLoaded = false;
    private static IntPtr replayBytesPtr;

    private static readonly Memory.Replacer alwaysRecordReplacer = new("24 06 3C 02 75 08 48 8B CB E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer = new("41 FF C6 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84", new byte[] { 0x90, 0x90, 0x90 }, true);

    [Signature("76 BA 48 8D 0D", ScanType = ScanType.StaticAddress)]
    public static Structures.FFXIVReplay* ffxivReplay;

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
    [Signature("40 55 57 48 8D 6C 24 B1 48 81 EC 98 00 00 00")]
    private static Hook<InitializeRecordingDelegate> InitializeRecordingHook;
    private static void InitializeRecordingDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        InitializeRecordingHook.Original(ffxivReplay);
        BeginRecording();
    }

    private delegate void BeginPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte canEnter);
    [Signature("E8 ?? ?? ?? ?? 0F B7 17 48 8B CB")]
    private static Hook<BeginPlaybackDelegate> BeginPlaybackHook;
    private static void BeginPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte allowed)
    {
        BeginPlaybackHook.Original(ffxivReplay, allowed);
        if (allowed != 0)
            ReadReplay(ffxivReplay->currentReplaySlot);
    }

    private delegate Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 53 48 83 EC 20 8B 81 90 00 00 00")]
    private static Hook<GetReplayDataSegmentDelegate> GetReplayDataSegmentHook;
    public static Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        if (!replayLoaded)
            return GetReplayDataSegmentHook.Original(ffxivReplay);

        if (ffxivReplay->replayHeader.replayLength == 0)
            return null;

        ffxivReplay->dataOffset = 0;

        return (Structures.FFXIVReplay.ReplayDataSegment*)((long)replayBytesPtr + 0x364 + ffxivReplay->overallDataOffset);
    }

    public static void ReadReplay(int slot) => ReadReplay($"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat");

    public static void ReadReplay(string replayName)
    {
        try
        {
            var file = new FileInfo(Path.Combine(replayFolder, replayName));
            if (!file.Exists) return;

            if (replayLoaded)
                Marshal.FreeHGlobal(replayBytesPtr);

            using var fs = File.OpenRead(file.FullName);
            replayBytesPtr = Marshal.AllocHGlobal((int)fs.Length);
            _ = fs.Read(new Span<byte>((void*)replayBytesPtr, (int)fs.Length));
            replayLoaded = true;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay {replayName}\n{e}");
            replayLoaded = false;
        }
    }

    // E8 ?? ?? ?? ?? EB 10 41 83 78 04 00 EndPlayback
    // 48 89 5C 24 10 55 48 8B EC 48 81 EC 80 00 00 00 48 8B 05 Something to do with loading
    // E8 ?? ?? ?? ?? F6 83 12 07 00 00 04 PlaybackUpdate
    public static void Initialize()
    {
        // TODO change back to static whenever support is added
        //SignatureHelper.Initialise(typeof(Game));
        SignatureHelper.Initialise(new Game());
        InitializeRecordingHook.Enable();
        BeginPlaybackHook.Enable();
        GetReplayDataSegmentHook.Enable();

        if ((ffxivReplay->playbackControls & 0x40) != 0)
            ReadReplay(ffxivReplay->currentReplaySlot);
    }

    public static void Dispose()
    {
        InitializeRecordingHook?.Dispose();
        BeginPlaybackHook?.Dispose();
        GetReplayDataSegmentHook?.Dispose();

        if (!replayLoaded) return;
        Marshal.FreeHGlobal(replayBytesPtr);
        replayLoaded = false;
    }
}