// Keeps quest progress the same for every player in a group.
//
// How it works:
//   - Every few seconds it compares each member's profile with how it looked last time. Anything a
//     player gained in the menus (hand-ins, accepted quests, story choices) is added to the group's
//     ledger, and the ledger is copied to every member.
//   - At the end of a raid the game client sends its own copy of the profile, which replaces the
//     server's quests, counters and story variables. That copy doesn't include progress made by
//     friends while the player was away, so the mod works out the player's gains from what their
//     client last knew (their "client view"), adds those to the ledger, and copies the ledger back.
//   - Quests are only ever changed for players who have that quest started. Nobody gets a quest
//     turned in for them: when the steps are done, each player turns it in and gets their own rewards.
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Path = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils.Cloners;

namespace SharedQuests;

sealed class Group(int index)
{
    public readonly int Index = index;
    public readonly List<string> Names = [];
    public readonly List<MongoId> Members = [];
    public readonly State Ledger = new();
    public readonly Dictionary<string, RepeatableSet> Repeatables = new();
}

sealed class RepeatableSet
{
    public long EndTime;
    public MongoId? Id;
    public List<RepeatableQuest> Active = [];
    public Dictionary<MongoId, ChangeRequirement> ChangeRequirement = new();
}

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad)]
public class SharedQuestService(
    ISptLogger<SharedQuestService> logger,
    SaveServer saveServer,
    QuestController questController,
    RepeatableQuestController repeatableQuestController,
    EventOutputHolder eventOutputHolder,
    ICloner cloner,
    IServiceProvider services
) : IOnLoad, IOnUpdate
{
    internal static SharedQuestService? Instance;

    readonly object _gate = new();
    SyncConfig _config = new();
    readonly List<Group> _groups = [];
    readonly Dictionary<MongoId, Group> _groupOf = new();
    readonly Dictionary<MongoId, State> _lastSeen = new();
    readonly Dictionary<MongoId, State> _clientView = new();
    readonly Dictionary<MongoId, string> _raidMatch = new();
    readonly Dictionary<string, (DateTime Seen, Dictionary<string, double> Gains)> _sameRaidGains = new();
    bool _sameRaidMax;
    object? _fikaMatchService;
    MethodInfo? _fikaGetMatchIdByProfile;
    [ThreadStatic] static bool _applying;

    string ModDir => Path.GetDirectoryName(typeof(SharedQuestService).Assembly.Location)!;

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    //====================[ Start-up ]====================
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        _config = SyncConfig.Load(Path.Combine(ModDir, "config.json"));
        if (!_config.Enabled)
        {
            logger.Info("[Shared Quests] Turned off in config.json");
            return Task.CompletedTask;
        }

        for (var i = 0; i < _config.Groups.Count; i++)
        {
            var group = new Group(i);
            group.Names.AddRange(_config.Groups[i].Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()));
            _groups.Add(group);
        }

        _sameRaidMax = _config.SameRaidCounting.ToLowerInvariant() switch
        {
            "max" => true,
            "sum" => false,
            _ => FikaSharesProgress(),
        };

        FindFika();
        Patches.Apply(new Harmony("akvxxx16.sharedquests"), logger);
        ResolveMembers();
        logger.Info(
            $"[Shared Quests] {_groups.Count} group(s); same-raid counting: {(_sameRaidMax ? "max" : "sum")}; "
                + $"story {(_config.SyncStory ? "on" : "off")}; dailies/weeklies {(_config.SyncRepeatables ? "on" : "off")}"
        );
        return Task.CompletedTask;
    }

    public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        if (!_config.Enabled || _groups.Count == 0) return Task.FromResult(true);
        if (secondsSinceLastRun < Math.Max(1, _config.CheckIntervalSeconds)) return Task.FromResult(false);
        try
        {
            Poll();
        }
        catch (Exception e)
        {
            logger.Error($"[Shared Quests] Check failed: {e}");
        }
        return Task.FromResult(true);
    }

    // Reads Fika's own config: when sharedQuestProgression is on, Fika already gives everyone in a raid
    // each other's kills, so gains from the same raid must not be added twice.
    bool FikaSharesProgress()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(ModDir, "..", "fika-server", "assets", "configs", "fika.jsonc"));
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
            );
            return doc.RootElement.TryGetProperty("client", out var client)
                && client.TryGetProperty("sharedQuestProgression", out var shared)
                && shared.ValueKind == JsonValueKind.True;
        }
        catch (Exception e)
        {
            logger.Warning($"[Shared Quests] Couldn't read Fika's config, adding same-raid gains together: {e.Message}");
            return false;
        }
    }

    void FindFika()
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("FikaServer.Services.MatchService", false))
                .FirstOrDefault(t => t is not null);
            if (type is null) return;
            _fikaMatchService = services.GetService(type);
            _fikaGetMatchIdByProfile = type.GetMethod("GetMatchIdByProfile", [typeof(MongoId)]);
        }
        catch (Exception e)
        {
            logger.Warning($"[Shared Quests] Fika's match service wasn't found, raids together count as separate: {e.Message}");
        }
    }

    string? FikaMatchOf(MongoId sessionId)
    {
        if (_fikaMatchService is null || _fikaGetMatchIdByProfile is null) return null;
        try
        {
            return _fikaGetMatchIdByProfile.Invoke(_fikaMatchService, [sessionId])?.ToString();
        }
        catch
        {
            return null;
        }
    }

    //====================[ Groups ]====================
    void ResolveMembers()
    {
        var profiles = saveServer.GetProfiles();
        foreach (var group in _groups)
        {
            foreach (var name in group.Names)
            {
                var match = profiles.FirstOrDefault(kv =>
                    kv.Key.ToString() == name
                    || string.Equals(kv.Value.ProfileInfo?.Username, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Value.CharacterData?.PmcData?.Info?.Nickname, name, StringComparison.OrdinalIgnoreCase)
                );
                if (match.Value is null || match.Value.CharacterData?.PmcData?.Info is null) continue;
                var id = match.Key;
                if (group.Members.Contains(id)) continue;
                if (_groupOf.TryGetValue(id, out var other))
                {
                    logger.Warning($"[Shared Quests] {name} is in more than one group; keeping group {other.Index + 1}");
                    continue;
                }
                group.Members.Add(id);
                _groupOf[id] = group;
                logger.Info($"[Shared Quests] Group {group.Index + 1}: {Describe(id)}");
            }
        }
    }

    // Finds a player's group. A profile made after the server started isn't known yet, so look again
    // before deciding the player isn't in a group; otherwise their first actions wouldn't be shared.
    bool TryGroup(MongoId id, out Group group)
    {
        lock (_gate)
        {
            if (_groupOf.TryGetValue(id, out group!)) return true;
            if (!_groups.Any(g => g.Members.Count < g.Names.Count)) return false;
            ResolveMembers();
            return _groupOf.TryGetValue(id, out group!);
        }
    }

    bool InGroup(MongoId id) => TryGroup(id, out _);

    string Describe(MongoId id)
    {
        var p = saveServer.GetProfiles().GetValueOrDefault(id);
        return p?.CharacterData?.PmcData?.Info?.Nickname ?? p?.ProfileInfo?.Username ?? id.ToString();
    }

    SptProfile? ProfileOf(MongoId id)
    {
        var p = saveServer.GetProfiles().GetValueOrDefault(id);
        return p?.CharacterData?.PmcData?.Quests is null ? null : p;
    }

    //====================[ Menu changes ]====================
    // Picks up changes made since the last check and shares them with the rest of the group.
    internal void Poll()
    {
        lock (_gate)
        {
            if (_groups.Any(g => g.Members.Count < g.Names.Count)) ResolveMembers();
            foreach (var group in _groups) PollGroup(group);
        }
    }

    void PollGroup(Group group)
    {
        var changed = false;
        foreach (var id in group.Members)
        {
            var profile = ProfileOf(id);
            if (profile is null) continue;

            var match = FikaMatchOf(id);
            if (match is not null) _raidMatch[id] = match;

            var now = State.From(profile, _config.SyncStory);
            if (_lastSeen.TryGetValue(id, out var before))
            {
                if (group.Ledger.AddChanges(before, now, _config.SyncStory)) changed = true;
                if (_clientView.TryGetValue(id, out var view)) view.AddChanges(before, now, _config.SyncStory);
            }
            else
            {
                // First look at this player since the server started.
                group.Ledger.Merge(now, _config.SyncStory);
                _clientView[id] = now.Clone();
                changed = true;
            }
            _lastSeen[id] = now;
        }

        if (changed) Share(group);
    }

    // Copies the ledger to every member, then remembers how each member now looks.
    void Share(Group group)
    {
        foreach (var id in group.Members)
        {
            var profile = ProfileOf(id);
            if (profile is null) continue;
            var result = group.Ledger.ApplyTo(profile, _config.SyncStory, Now);
            if (result is not null && _config.LogChanges) logger.Info($"[Shared Quests] {Describe(id)}: {result}");
            _lastSeen[id] = State.From(profile, _config.SyncStory);
        }
    }

    //====================[ Profile downloads ]====================
    // The game client downloads the whole profile when it opens the menu (after login and after a raid),
    // so from then on it knows everything the server has.
    internal void BeforeProfileDownload(MongoId sessionId)
    {
        if (InGroup(sessionId)) Poll();
    }

    internal void AfterProfileDownload(MongoId sessionId)
    {
        lock (_gate)
        {
            if (!InGroup(sessionId)) return;
            var profile = ProfileOf(sessionId);
            if (profile is null) return;
            _clientView[sessionId] = State.From(profile, _config.SyncStory);
        }
    }

    //====================[ Live sync (client plugin) ]====================
    // See LiveSync.cs. Records what the player's game has, then answers with what the server has.
    internal SyncReply ClientSync(MongoId sessionId, SyncRequest? request)
    {
        var off = new SyncReply { Enabled = false };
        if (!_config.Enabled || !_config.LiveSync || !InGroup(sessionId)) return off;

        Poll();
        lock (_gate)
        {
            var profile = ProfileOf(sessionId);
            var pmc = profile?.CharacterData?.PmcData;
            if (profile is null || pmc is null) return off;
            var server = State.From(profile, _config.SyncStory);

            if (request?.Quests is not null && request.Counters is not null)
            {
                var quests = new Dictionary<string, (QuestStatusEnum, HashSet<string>)>();
                foreach (var (q, report) in request.Quests)
                    quests[q] = ((QuestStatusEnum)report.Status, new HashSet<string>(report.Conditions ?? []));
                var view = _clientView.TryGetValue(sessionId, out var v) ? v : server.Clone();
                view.SetQuestsFromClient(request.Counters, quests, server);
                _clientView[sessionId] = view;
            }

            var reply = new SyncReply { Enabled = true };
            var active = new HashSet<string>();
            foreach (var quest in pmc.Quests ?? [])
            {
                if (!State.IsActive(quest.Status)) continue;
                var q = quest.QId.ToString();
                active.Add(q);
                reply.Quests[q] = new QuestReport { Status = (int)quest.Status, Conditions = (quest.CompletedConditions ?? []).ToList() };
            }
            foreach (var (id, counter) in pmc.TaskConditionCounters ?? [])
            {
                var q = counter.SourceId?.ToString() ?? "";
                if (active.Contains(q)) reply.Counters[id.ToString()] = new CounterReport { Value = counter.Value ?? 0, Quest = q };
            }
            return reply;
        }
    }

    //====================[ End of raid ]====================
    internal void BeforeRaidEnd(MongoId sessionId)
    {
        if (InGroup(sessionId)) Poll();
    }

    internal void AfterRaidEnd(MongoId sessionId)
    {
        lock (_gate)
        {
            if (!TryGroup(sessionId, out var group)) return;
            var profile = ProfileOf(sessionId);
            if (profile is null) return;

            // What the client sent back, compared with what the client knew when the raid started.
            var fromClient = State.From(profile, _config.SyncStory);
            var knewBefore = _clientView.GetValueOrDefault(sessionId) ?? _lastSeen.GetValueOrDefault(sessionId) ?? fromClient;

            Dictionary<string, double>? sameRaid = null;
            var match = FikaMatchOf(sessionId) ?? _raidMatch.GetValueOrDefault(sessionId);
            if (_sameRaidMax && match is not null)
            {
                var key = $"{group.Index}:{match}";
                if (!_sameRaidGains.TryGetValue(key, out var entry)) entry = (DateTime.UtcNow, new Dictionary<string, double>());
                _sameRaidGains[key] = (DateTime.UtcNow, entry.Gains);
                sameRaid = entry.Gains;
            }
            _raidMatch.Remove(sessionId);
            foreach (var old in _sameRaidGains.Where(kv => DateTime.UtcNow - kv.Value.Seen > TimeSpan.FromHours(3)).Select(kv => kv.Key).ToList())
                _sameRaidGains.Remove(old);

            group.Ledger.AddChanges(knewBefore, fromClient, _config.SyncStory, sameRaid);
            _clientView[sessionId] = fromClient.Clone();
            if (_config.LogChanges) logger.Info($"[Shared Quests] {Describe(sessionId)} finished a raid{(match is not null ? " (Fika match " + match + ")" : "")}");
            Share(group);
        }
    }

    //====================[ Accepting quests ]====================
    // Stops a stale client from accepting a quest again after the mod already started it for that player:
    // accepting twice would hand out the quest's start rewards twice and wipe its progress.
    internal bool AlreadyStarted(MongoId sessionId, MongoId questId)
    {
        if (_applying || !_config.ShareAccept) return false;
        lock (_gate)
        {
            if (!InGroup(sessionId)) return false;
            var status = ProfileOf(sessionId)?.CharacterData?.PmcData?.Quests?.FirstOrDefault(q => q.QId == questId)?.Status;
            return status is QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish;
        }
    }

    internal object EmptyResponse(MongoId sessionId) => eventOutputHolder.GetOutput(sessionId);

    internal QuestStatusEnum? StatusOf(MongoId sessionId, MongoId questId) =>
        ProfileOf(sessionId)?.CharacterData?.PmcData?.Quests?.FirstOrDefault(q => q.QId == questId)?.Status;

    internal void AfterAccept(MongoId sessionId, AcceptQuestRequestData request, QuestStatusEnum? before, bool repeatable)
    {
        if (_applying) return;
        lock (_gate)
        {
            if (!TryGroup(sessionId, out var group)) return;
            var questId = request.QuestId;

            // Restarting a failed quest starts it from nothing for everyone.
            if (before is QuestStatusEnum.Fail or QuestStatusEnum.FailRestartable or QuestStatusEnum.MarkedAsFailed)
                group.Ledger.ResetQuest(questId.ToString());

            if (_config.ShareAccept)
            {
                foreach (var other in group.Members.Where(m => m != sessionId))
                {
                    var profile = ProfileOf(other);
                    var pmc = profile?.CharacterData?.PmcData;
                    if (pmc is null) continue;
                    if (!CanAccept(other, pmc, questId, repeatable)) continue;

                    _applying = true;
                    try
                    {
                        var copy = new AcceptQuestRequestData { QuestId = questId, Type = request.Type, Action = request.Action };
                        if (repeatable) repeatableQuestController.AcceptRepeatableQuest(pmc, copy, other);
                        else questController.AcceptQuest(pmc, copy, other);
                        if (_config.LogChanges) logger.Info($"[Shared Quests] Accepted {questId} for {Describe(other)}");
                    }
                    catch (Exception e)
                    {
                        logger.Warning($"[Shared Quests] Couldn't accept {questId} for {Describe(other)}: {e.Message}");
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
            }
        }
        Poll();
    }

    bool CanAccept(MongoId id, SPTarkov.Server.Core.Models.Eft.Common.PmcData pmc, MongoId questId, bool repeatable)
    {
        var existing = pmc.Quests?.FirstOrDefault(q => q.QId == questId);
        if (repeatable)
            return existing is null
                && (pmc.RepeatableQuests ?? []).Any(r => (r.ActiveQuests ?? []).Any(q => q.Id == questId));

        if (existing is not null && existing.Status != QuestStatusEnum.AvailableForStart) return false;
        try
        {
            // The quest list the client gets also checks level, faction and earlier quests.
            return questController.GetClientQuests(id).Any(q => q.Id == questId);
        }
        catch
        {
            return false;
        }
    }

    //====================[ Dailies and weeklies ]====================
    // The first member to get a new set each cycle sets it for the whole group.
    internal Dictionary<string, long?> RepeatableEndTimes(MongoId sessionId) =>
        (ProfileOf(sessionId)?.CharacterData?.PmcData?.RepeatableQuests ?? [])
            .Where(r => r.Name is not null)
            .ToDictionary(r => r.Name!, r => r.EndTime);

    internal void AfterRepeatablesRequested(MongoId sessionId, Dictionary<string, long?> endTimesBefore, List<PmcDataRepeatableQuest>? sentToClient)
    {
        AdoptGroupRepeatables(sessionId, endTimesBefore);

        // SPT answers with copies of the profile's lists, so update the answer to match the profile.
        var pmc = ProfileOf(sessionId)?.CharacterData?.PmcData;
        if (sentToClient is null || pmc?.RepeatableQuests is null) return;
        foreach (var sent in sentToClient)
        {
            var mine = pmc.RepeatableQuests.FirstOrDefault(r => r.Name == sent.Name);
            if (mine is null) continue;
            sent.ActiveQuests = mine.ActiveQuests;
            sent.EndTime = mine.EndTime;
            sent.Id = mine.Id;
            sent.ChangeRequirement = mine.ChangeRequirement;
        }
    }

    void AdoptGroupRepeatables(MongoId sessionId, Dictionary<string, long?> endTimesBefore)
    {
        if (!_config.SyncRepeatables) return;
        lock (_gate)
        {
            if (!TryGroup(sessionId, out var group)) return;
            var pmc = ProfileOf(sessionId)?.CharacterData?.PmcData;
            if (pmc?.RepeatableQuests is null) return;
            var now = (long)Now;

            foreach (var mine in pmc.RepeatableQuests)
            {
                if (mine.Name is null) continue;
                var regenerated = endTimesBefore.GetValueOrDefault(mine.Name) != mine.EndTime;
                group.Repeatables.TryGetValue(mine.Name, out var shared);

                if (regenerated && shared is not null && shared.EndTime > now + 1)
                {
                    // Someone in the group already got this cycle's set: use theirs.
                    mine.ActiveQuests = shared.Active.Select(q => cloner.Clone(q)!).ToList();
                    mine.EndTime = shared.EndTime;
                    mine.Id = shared.Id;
                    mine.ChangeRequirement ??= new();
                    foreach (var (k, v) in shared.ChangeRequirement) mine.ChangeRequirement[k] = cloner.Clone(v)!;
                    if (_config.LogChanges) logger.Info($"[Shared Quests] {Describe(sessionId)}: using the group's {mine.Name} quests");
                }
                else if (shared is null || regenerated || (mine.EndTime ?? 0) > shared.EndTime + 1)
                {
                    group.Repeatables[mine.Name] = Capture(mine);
                }
            }
        }
    }

    RepeatableSet Capture(PmcDataRepeatableQuest r) =>
        new()
        {
            EndTime = r.EndTime ?? 0,
            Id = r.Id,
            Active = (r.ActiveQuests ?? []).Select(q => cloner.Clone(q)!).ToList(),
            ChangeRequirement = (r.ChangeRequirement ?? new()).ToDictionary(kv => kv.Key, kv => cloner.Clone(kv.Value)!),
        };

    internal Dictionary<string, HashSet<MongoId>> ActiveRepeatableIds(MongoId sessionId) =>
        (ProfileOf(sessionId)?.CharacterData?.PmcData?.RepeatableQuests ?? [])
            .Where(r => r.Name is not null)
            .ToDictionary(r => r.Name!, r => (r.ActiveQuests ?? []).Select(q => q.Id).ToHashSet());

    // A reroll replaces one quest; give the others the same replacement if they haven't accepted the old one.
    internal void AfterReroll(MongoId sessionId, MongoId replaced, Dictionary<string, HashSet<MongoId>> idsBefore)
    {
        if (!_config.SyncRepeatables) return;
        lock (_gate)
        {
            if (!TryGroup(sessionId, out var group)) return;
            var pmc = ProfileOf(sessionId)?.CharacterData?.PmcData;
            if (pmc?.RepeatableQuests is null) return;

            foreach (var mine in pmc.RepeatableQuests)
            {
                if (mine.Name is null || !idsBefore.TryGetValue(mine.Name, out var before) || !before.Contains(replaced)) continue;
                var added = (mine.ActiveQuests ?? []).Where(q => !before.Contains(q.Id)).ToList();
                if (added.Count != 1) continue;
                var newQuest = added[0];
                var change = mine.ChangeRequirement?.GetValueOrDefault(newQuest.Id);

                if (group.Repeatables.TryGetValue(mine.Name, out var shared))
                {
                    var i = shared.Active.FindIndex(q => q.Id == replaced);
                    if (i >= 0) shared.Active[i] = cloner.Clone(newQuest)!;
                    if (change is not null) shared.ChangeRequirement[newQuest.Id] = cloner.Clone(change)!;
                }

                foreach (var other in group.Members.Where(m => m != sessionId))
                {
                    var otherPmc = ProfileOf(other)?.CharacterData?.PmcData;
                    var theirs = otherPmc?.RepeatableQuests?.FirstOrDefault(r => r.Name == mine.Name);
                    if (theirs?.ActiveQuests is null || theirs.EndTime != mine.EndTime) continue;
                    if ((otherPmc!.Quests ?? []).Any(q => q.QId == replaced)) continue;
                    var j = theirs.ActiveQuests.FindIndex(q => q.Id == replaced);
                    if (j < 0) continue;
                    theirs.ActiveQuests[j] = cloner.Clone(newQuest)!;
                    theirs.ChangeRequirement ??= new();
                    theirs.ChangeRequirement.Remove(replaced);
                    if (change is not null) theirs.ChangeRequirement[newQuest.Id] = cloner.Clone(change)!;
                    if (_config.LogChanges) logger.Info($"[Shared Quests] {Describe(other)}: rerolled {mine.Name} quest to match");
                }
            }
        }
    }
}
