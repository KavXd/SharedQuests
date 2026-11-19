// Hooks into SPT's quest and raid code. Each hook only tells SharedQuestService what happened;
// a failure in here is logged and never stops the game's own code from running.
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.InRaid;

namespace SharedQuests;

static class Patches
{
    static ISptLogger<SharedQuestService>? _logger;

    public static void Apply(Harmony harmony, ISptLogger<SharedQuestService> logger)
    {
        _logger = logger;
        Patch(harmony, typeof(ProfileController), nameof(ProfileController.GetCompleteProfile), nameof(DownloadPrefix), nameof(DownloadPostfix));
        Patch(harmony, typeof(LocationLifecycleService), "HandlePostRaidPmc", nameof(RaidEndPrefix), nameof(RaidEndPostfix));
        Patch(harmony, typeof(QuestController), nameof(QuestController.AcceptQuest), nameof(AcceptPrefix), nameof(AcceptPostfix));
        Patch(harmony, typeof(RepeatableQuestController), nameof(RepeatableQuestController.AcceptRepeatableQuest), nameof(AcceptRepeatablePrefix), nameof(AcceptRepeatablePostfix));
        Patch(harmony, typeof(RepeatableQuestController), nameof(RepeatableQuestController.GetClientRepeatableQuests), nameof(RepeatablesPrefix), nameof(RepeatablesPostfix));
        Patch(harmony, typeof(RepeatableQuestController), nameof(RepeatableQuestController.ChangeRepeatableQuest), nameof(RerollPrefix), nameof(RerollPostfix));
    }

    static void Patch(Harmony harmony, Type type, string method, string prefix, string postfix)
    {
        var target = AccessTools.Method(type, method);
        if (target is null)
        {
            _logger?.Error($"[Shared Quests] {type.Name}.{method} wasn't found; that part of the sync is off");
            return;
        }
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(Patches), prefix)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), postfix))
        );
    }

    static void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _logger?.Error($"[Shared Quests] {what} failed: {e}");
        }
    }

    static SharedQuestService? S => SharedQuestService.Instance;

    // Profile download
    static void DownloadPrefix(MongoId sessionId) => Safe("Profile download", () => S?.BeforeProfileDownload(sessionId));

    static void DownloadPostfix(MongoId sessionId) => Safe("Profile download", () => S?.AfterProfileDownload(sessionId));

    // End of a PMC raid
    static void RaidEndPrefix(MongoId sessionId) => Safe("Raid end", () => S?.BeforeRaidEnd(sessionId));

    static void RaidEndPostfix(MongoId sessionId) => Safe("Raid end", () => S?.AfterRaidEnd(sessionId));

    // Accepting a quest
    static bool AcceptPrefix(PmcData pmcData, AcceptQuestRequestData acceptedQuest, MongoId sessionID, ref ItemEventRouterResponse __result, out QuestStatusEnum? __state)
    {
        __state = null;
        try
        {
            if (S is null) return true;
            __state = S.StatusOf(sessionID, acceptedQuest.QuestId);
            if (!S.AlreadyStarted(sessionID, acceptedQuest.QuestId)) return true;
            __result = (ItemEventRouterResponse)S.EmptyResponse(sessionID);
            return false;
        }
        catch (Exception e)
        {
            _logger?.Error($"[Shared Quests] Accept check failed: {e}");
            return true;
        }
    }

    static void AcceptPostfix(AcceptQuestRequestData acceptedQuest, MongoId sessionID, QuestStatusEnum? __state, bool __runOriginal)
    {
        if (!__runOriginal) return;
        Safe("Accept", () => S?.AfterAccept(sessionID, acceptedQuest, __state, false));
    }

    static bool AcceptRepeatablePrefix(PmcData pmcData, AcceptQuestRequestData acceptedQuest, MongoId sessionID, ref ItemEventRouterResponse __result, out QuestStatusEnum? __state) =>
        AcceptPrefix(pmcData, acceptedQuest, sessionID, ref __result, out __state);

    static void AcceptRepeatablePostfix(AcceptQuestRequestData acceptedQuest, MongoId sessionID, QuestStatusEnum? __state, bool __runOriginal)
    {
        if (!__runOriginal) return;
        Safe("Accept", () => S?.AfterAccept(sessionID, acceptedQuest, __state, true));
    }

    // Dailies and weeklies
    static void RepeatablesPrefix(MongoId sessionID, out Dictionary<string, long?>? __state)
    {
        Dictionary<string, long?>? endTimes = null;
        Safe("Repeatables", () => endTimes = S?.RepeatableEndTimes(sessionID));
        __state = endTimes;
    }

    static void RepeatablesPostfix(MongoId sessionID, Dictionary<string, long?>? __state, List<PmcDataRepeatableQuest>? __result)
    {
        if (__state is null) return;
        Safe("Repeatables", () => S?.AfterRepeatablesRequested(sessionID, __state, __result));
    }

    static void RerollPrefix(RepeatableQuestChangeRequest changeRequest, MongoId sessionID, out Dictionary<string, HashSet<MongoId>>? __state)
    {
        Dictionary<string, HashSet<MongoId>>? ids = null;
        Safe("Reroll", () => ids = S?.ActiveRepeatableIds(sessionID));
        __state = ids;
    }

    static void RerollPostfix(RepeatableQuestChangeRequest changeRequest, MongoId sessionID, Dictionary<string, HashSet<MongoId>>? __state)
    {
        if (__state is null) return;
        Safe("Reroll", () => S?.AfterReroll(sessionID, changeRequest.QuestId, __state));
    }
}
