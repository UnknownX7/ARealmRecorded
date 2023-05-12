using System;
using Hypostasis.Game.Structures;

namespace ARealmRecorded;

public static unsafe class ReplayExtensions
{
    public static byte FindPreviousChapterFromTime(this ref FFXIVReplay.ChapterArray chapters, uint ms)
    {
        for (byte i = (byte)(chapters.length - 1); i > 0; i--)
            if (chapters[i]->ms <= ms) return i;
        return 0;
    }

    public static byte GetCurrentChapter(this ref ContentsReplayModule contentsReplayModule) => contentsReplayModule.chapters.FindPreviousChapterFromTime((uint)(contentsReplayModule.seek * 1000));

    public static byte FindPreviousChapterType(this ref FFXIVReplay.ChapterArray chapters, byte chapter, byte type)
    {
        for (byte i = chapter; i > 0; i--)
            if (chapters[i]->type == type) return i;
        return 0;
    }

    public static byte FindPreviousChapterType(this ref ContentsReplayModule contentsReplayModule, byte type) => contentsReplayModule.chapters.FindPreviousChapterType(contentsReplayModule.GetCurrentChapter(), type);

    public static byte FindNextChapterType(this ref FFXIVReplay.ChapterArray chapters, byte chapter, byte type)
    {
        for (byte i = ++chapter; i < chapters.length; i++)
            if (chapters[i]->type == type) return i;
        return 0;
    }

    public static byte FindNextChapterType(this ref ContentsReplayModule contentsReplayModule, byte type) => contentsReplayModule.chapters.FindNextChapterType(contentsReplayModule.GetCurrentChapter(), type);

    public static byte GetPreviousStartChapter(this ref FFXIVReplay.ChapterArray chapterArray, byte chapter) =>
        chapterArray.FindPreviousChapterType(chapter, 2) is var previousStart && previousStart > 0
            ? chapterArray.FindPreviousChapterType(--previousStart, 2)
            : (byte)0;

    public static byte GetPreviousStartChapter(this ref ContentsReplayModule contentsReplayModule) => contentsReplayModule.chapters.GetPreviousStartChapter(contentsReplayModule.GetCurrentChapter());

    public static FFXIVReplay.DataSegment* FindNextDataSegment(this ref FFXIVReplay replay, uint ms, out uint offset)
    {
        offset = 0;

        FFXIVReplay.DataSegment* segment;
        while ((segment = replay.GetDataSegment(offset)) != null)
        {
            if (segment->ms >= ms) return segment;
            offset += segment->Length;
        }

        return null;
    }

    public static void FixNextReplaySaveSlot(this ref ContentsReplayModule contentsReplayModule)
    {
        if (ARealmRecorded.Config.MaxAutoRenamedReplays <= 0 && !contentsReplayModule.savedReplayHeaders[contentsReplayModule.nextReplaySaveSlot].IsLocked) return;

        for (byte i = 0; i < 3; i++)
        {
            if (i != 2)
            {
                var header = contentsReplayModule.savedReplayHeaders[i];
                if (header.IsLocked) continue;
            }

            contentsReplayModule.nextReplaySaveSlot = i;
            return;
        }
    }

    public static void SetSavedReplayCIDs(this ref ContentsReplayModule contentsReplayModule, ulong cID)
    {
        if (contentsReplayModule.savedReplayHeaders == null) return;

        for (int i = 0; i < 3; i++)
        {
            var header = contentsReplayModule.savedReplayHeaders[i];
            if (!header.IsValid) continue;
            header.localCID = cID;
            contentsReplayModule.savedReplayHeaders[i] = header;
        }
    }

    public static (int pulls, TimeSpan longestPull) GetPullInfo(this ref FFXIVReplay replay)
    {
        var pulls = 0;
        var longestPull = TimeSpan.Zero;
        for (byte j = 0; j < replay.chapters.length; j++)
        {
            var chapter = replay.chapters[j];
            if (chapter->type != 2 && j != 0) continue;

            if (j < replay.chapters.length - 1)
            {
                var nextChapter = replay.chapters[j + 1];
                if (nextChapter->type == 1)
                {
                    chapter = nextChapter;
                    j++;
                }
            }

            var nextStartMS = replay.chapters.FindNextChapterType(j, 2) is var nextStart && nextStart > 0 ? replay.chapters[nextStart]->ms : replay.header.totalMS;
            var ms = (int)(nextStartMS - chapter->ms);
            if (ms > 30_000)
                pulls++;

            var timeSpan = new TimeSpan(0, 0, 0, 0, ms);
            if (timeSpan > longestPull)
                longestPull = timeSpan;
        }
        return (pulls, longestPull);
    }
}