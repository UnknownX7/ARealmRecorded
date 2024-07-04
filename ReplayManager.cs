using System.IO;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Hypostasis.Game.Structures;

namespace ARealmRecorded;

public static unsafe class ReplayManager
{
    private static FFXIVReplay* loadedReplay;
    private static byte quickLoadChapter;
    private static byte seekingChapter;
    private static uint seekingOffset;

    private static readonly AsmPatch removeProcessingLimitPatch = new("41 FF C4 48 39 43 38", new byte?[] { 0x90, 0x90, 0x90 }, true);
    private static readonly AsmPatch removeProcessingLimitPatch2 = new("0F 87 7C 02 00 00 48 8B 0D", new byte?[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90}, true);
    private static readonly AsmPatch forceFastForwardPatch = new("0F 83 ?? ?? ?? ?? 41 0F B7 46 02 4D 8D 46 0C", new byte?[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });

    public static void PlaybackUpdate(ContentsReplayModule* contentsReplayModule)
    {
        if (loadedReplay == null) return;

        contentsReplayModule->dataLoadType = 0;
        contentsReplayModule->dataOffset = 0;

        if (quickLoadChapter < 2) return;

        var seekedTime = contentsReplayModule->chapters[seekingChapter]->ms;
        if (seekedTime > contentsReplayModule->seek.ToMilliseconds()) return;

        DoQuickLoad();
    }

    public static FFXIVReplay.DataSegment* GetReplayDataSegment(ContentsReplayModule* contentsReplayModule)
    {
        if (loadedReplay == null) return null;

        // Needs to be here to prevent infinite looping
        if (seekingOffset > 0 && seekingOffset <= contentsReplayModule->overallDataOffset)
        {
            forceFastForwardPatch.Disable();
            seekingOffset = 0;
        }

        // Absurdly hacky, but it works
        if (ARealmRecorded.Config.MaxSeekDelta <= 100 || contentsReplayModule->seekDelta >= ARealmRecorded.Config.MaxSeekDelta)
            removeProcessingLimitPatch2.Disable();
        else
            removeProcessingLimitPatch2.Enable();

        return loadedReplay->GetDataSegment((uint)contentsReplayModule->overallDataOffset);
    }

    public static void OnSetChapter(ContentsReplayModule* contentsReplayModule, byte chapter)
    {
        if (!ARealmRecorded.Config.EnableQuickLoad || chapter <= 0 || contentsReplayModule->chapters.length < 2 || contentsReplayModule->GetCurrentChapter() + 1 == chapter) return;
        quickLoadChapter = chapter;
        seekingChapter = 0;
        DoQuickLoad();
    }

    public static bool LoadReplay(int slot) => LoadReplay(Path.Combine(Game.replayFolder, Game.GetReplaySlotName(slot)));

    public static bool LoadReplay(string path)
    {
        var newReplay = Game.ReadReplay(path);
        if (newReplay == null) return false;

        if (loadedReplay != null)
            Marshal.FreeHGlobal((nint)loadedReplay);

        loadedReplay = newReplay;
        Common.ContentsReplayModule->replayHeader = loadedReplay->header;
        Common.ContentsReplayModule->chapters = loadedReplay->chapters;
        Common.ContentsReplayModule->dataLoadType = 0;

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

    public static void JumpToChapter(byte chapter)
    {
        var jumpChapter = Common.ContentsReplayModule->chapters[chapter];
        if (jumpChapter == null) return;
        Common.ContentsReplayModule->overallDataOffset = jumpChapter->offset;
        Common.ContentsReplayModule->seek = jumpChapter->ms / 1000f;
    }

    public static void JumpToTime(uint ms)
    {
        var segment = loadedReplay->FindNextDataSegment(ms, out var offset);
        if (segment == null) return;
        Common.ContentsReplayModule->overallDataOffset = offset;
        Common.ContentsReplayModule->seek = segment->ms / 1000f;
    }

    public static void JumpToTimeBeforeChapter(byte chapter, uint ms)
    {
        var jumpChapter = Common.ContentsReplayModule->chapters[chapter];
        if (jumpChapter == null) return;
        JumpToTime(jumpChapter->ms > ms ? jumpChapter->ms - ms : 0);
    }

    public static void SeekToTime(uint ms)
    {
        if (Common.ContentsReplayModule->IsLoadingChapter) return;

        var prevChapter = Common.ContentsReplayModule->chapters.FindPreviousChapterFromTime(ms);
        var segment = loadedReplay->FindNextDataSegment(ms, out var offset);
        if (segment == null) return;

        seekingOffset = offset;
        forceFastForwardPatch.Enable();
        if (Common.ContentsReplayModule->seek.ToMilliseconds() < segment->ms && prevChapter == Common.ContentsReplayModule->GetCurrentChapter())
            ContentsReplayModule.onSetChapter.Original(Common.ContentsReplayModule, prevChapter);
        else
            Common.ContentsReplayModule->SetChapter(prevChapter);
    }

    public static void ReplaySection(byte from, byte to)
    {
        if (from != 0 && Common.ContentsReplayModule->overallDataOffset < Common.ContentsReplayModule->chapters[from]->offset)
            JumpToChapter(from);

        seekingChapter = to;
        if (seekingChapter >= quickLoadChapter)
            quickLoadChapter = 0;
    }

    public static void DoQuickLoad()
    {
        if (seekingChapter == 0)
        {
            ReplaySection(0, 1);
            return;
        }

        var nextEvent = Common.ContentsReplayModule->chapters.FindNextChapterType(seekingChapter, 4);
        if (nextEvent != 0 && nextEvent < quickLoadChapter - 1)
        {
            var nextCountdown = Common.ContentsReplayModule->chapters.FindNextChapterType(nextEvent, 1);
            if (nextCountdown == 0 || nextCountdown > nextEvent + 2)
                nextCountdown = (byte)(nextEvent + 2);
            ReplaySection(nextEvent, nextCountdown);
            return;
        }

        for (int i = 0; i < 100; i++)
        {
            var o = CharacterManager.Instance()->BattleCharas[i].Value;
            if (o != null && o->Character.GameObject.GetObjectKind() == ObjectKind.BattleNpc)
                Game.DeleteCharacterAtIndex(i);
        }

        JumpToTimeBeforeChapter(Common.ContentsReplayModule->chapters.FindPreviousChapterType(quickLoadChapter, 2), 15_000);
        ReplaySection(0, quickLoadChapter);
    }

    public static void Dispose()
    {
        if (loadedReplay == null) return;

        if (Common.ContentsReplayModule->InPlayback)
        {
            Common.ContentsReplayModule->playbackControls |= 8; // Pause
            DalamudApi.PrintError("Plugin was unloaded, playback will be broken if the plugin or replay is not reloaded.");
        }

        Marshal.FreeHGlobal((nint)loadedReplay);
        loadedReplay = null;
    }
}