// The quest and story data that the mod keeps the same across a group, in a plain form that can be
// compared. A State is used three ways:
//   - a snapshot of one profile at one moment,
//   - a group's shared "ledger" (the combined progress of everyone in the group),
//   - a player's "client view" (what that player's game client currently believes).
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;

namespace SharedQuests;

public sealed class CounterInfo
{
    public string Type = "";
    public string SourceId = "";
}

public sealed class State
{
    // Task condition counters (kills, hand-in counts, ...), by counter id.
    public Dictionary<string, double> Counters = new();
    public Dictionary<string, CounterInfo> CounterMeta = new();
    // Completed condition ids, by quest id.
    public Dictionary<string, HashSet<string>> Conditions = new();
    // Quest status, by quest id.
    public Dictionary<string, QuestStatusEnum> Status = new();
    // Quests someone in the group finished all the steps of (ready to turn in, or turned in).
    public HashSet<string> Finishable = new();
    // Story: profile variables, dialogue nodes visited, and VisitAPI's own progress.
    public Dictionary<string, int> Variables = new();
    public HashSet<string> Dialogue = new();
    public HashSet<string> StoryRead = new();
    public HashSet<string> StoryDone = new();
    public HashSet<string> StorySkipped = new();
    public Dictionary<string, double> StoryCounts = new();

    public const string VisitKey = "visitapi";

    public static bool IsActive(QuestStatusEnum s) => s is QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish;

    public static bool IsDone(QuestStatusEnum s) => s is QuestStatusEnum.AvailableForFinish or QuestStatusEnum.Success;

    public State Clone()
    {
        var c = new State
        {
            Counters = new(Counters),
            CounterMeta = new(CounterMeta),
            Status = new(Status),
            Finishable = new(Finishable),
            Variables = new(Variables),
            Dialogue = new(Dialogue),
            StoryRead = new(StoryRead),
            StoryDone = new(StoryDone),
            StorySkipped = new(StorySkipped),
            StoryCounts = new(StoryCounts),
        };
        foreach (var (q, set) in Conditions) c.Conditions[q] = new HashSet<string>(set);
        return c;
    }

    //====================[ Reading a profile ]====================
    public static State From(SptProfile profile, bool story)
    {
        var s = new State();
        var pmc = profile.CharacterData?.PmcData;
        if (pmc is null) return s;

        foreach (var (id, counter) in pmc.TaskConditionCounters ?? [])
        {
            var key = id.ToString();
            s.Counters[key] = counter.Value ?? 0;
            s.CounterMeta[key] = new CounterInfo { Type = counter.Type ?? "", SourceId = counter.SourceId?.ToString() ?? "" };
        }

        foreach (var quest in pmc.Quests ?? [])
        {
            var q = quest.QId.ToString();
            s.Status[q] = quest.Status;
            s.Conditions[q] = new HashSet<string>(quest.CompletedConditions ?? []);
            if (IsDone(quest.Status)) s.Finishable.Add(q);
        }

        if (!story) return s;

        foreach (var (id, value) in pmc.Variables ?? []) s.Variables[id.ToString()] = value;
        foreach (var node in profile.DialogueProgress ?? []) s.Dialogue.Add(DialogueKey(node));

        var visit = ReadVisit(profile);
        if (visit is not null)
        {
            AddStrings(s.StoryRead, visit["read"]);
            var objectives = visit["objectives"] as JsonObject;
            AddStrings(s.StoryDone, objectives?["done"]);
            AddStrings(s.StorySkipped, objectives?["skipped"]);
            if (objectives?["counts"] is JsonObject counts)
                foreach (var (k, v) in counts)
                    if (v is JsonValue jv && jv.TryGetValue<double>(out var d)) s.StoryCounts[k] = d;
        }
        return s;
    }

    static string DialogueKey(NodePathTraveled n) => $"{n.TraderId}|{n.DialogueId}|{n.NodeId}";

    static void AddStrings(HashSet<string> set, JsonNode? node)
    {
        if (node is not JsonArray arr) return;
        foreach (var x in arr)
            if (x is JsonValue v && v.TryGetValue<string>(out var str) && !string.IsNullOrEmpty(str)) set.Add(str);
    }

    static JsonObject? ReadVisit(SptProfile profile)
    {
        if (profile.ExtensionData is null || !profile.ExtensionData.TryGetValue(VisitKey, out var raw) || raw is null) return null;
        try
        {
            var node = raw switch
            {
                JsonElement el => JsonNode.Parse(el.GetRawText()),
                JsonNode n => JsonNode.Parse(n.ToJsonString()),
                _ => JsonSerializer.SerializeToNode(raw),
            };
            return node as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    //====================[ Recording changes ]====================
    // Adds what changed between "before" and "after" (one player's own progress) to this state.
    // sameRaidGains, when given, holds the gains other players in the same Fika raid already added: Fika
    // already shared those kills between them, so only the part beyond what was already added counts.
    // Returns true when anything changed.
    public bool AddChanges(State before, State after, bool story, Dictionary<string, double>? sameRaidGains = null)
    {
        var changed = false;

        foreach (var (id, value) in after.Counters)
        {
            var old = before.Counters.GetValueOrDefault(id);
            var gain = value - old;
            if (gain <= 0) continue;

            if (sameRaidGains is not null)
            {
                var already = sameRaidGains.GetValueOrDefault(id);
                sameRaidGains[id] = Math.Max(already, gain);
                gain -= already;
                if (gain <= 0) continue;
            }

            var current = Counters.TryGetValue(id, out var have) ? have : old;
            Counters[id] = current + gain;
            if (after.CounterMeta.TryGetValue(id, out var meta)) CounterMeta[id] = meta;
            changed = true;
        }

        foreach (var (q, conditions) in after.Conditions)
        {
            before.Conditions.TryGetValue(q, out var oldSet);
            foreach (var c in conditions)
            {
                if (oldSet is not null && oldSet.Contains(c)) continue;
                if (!Conditions.TryGetValue(q, out var set)) Conditions[q] = set = new HashSet<string>();
                if (set.Add(c)) changed = true;
            }
        }

        foreach (var (q, status) in after.Status)
        {
            var was = before.Status.TryGetValue(q, out var w) ? w : QuestStatusEnum.Locked;
            if (Status.GetValueOrDefault(q) != status && was != status) Status[q] = status;
            if (IsDone(status) && !IsDone(was) && Finishable.Add(q)) changed = true;
        }

        if (!story) return changed;

        foreach (var (k, v) in after.Variables)
        {
            if (before.Variables.TryGetValue(k, out var old) && old == v) continue;
            if (Variables.TryGetValue(k, out var have) && have == v) continue;
            Variables[k] = v;
            changed = true;
        }

        changed |= AddNew(Dialogue, before.Dialogue, after.Dialogue);
        changed |= AddNew(StoryRead, before.StoryRead, after.StoryRead);
        changed |= AddNew(StoryDone, before.StoryDone, after.StoryDone);
        changed |= AddNew(StorySkipped, before.StorySkipped, after.StorySkipped);
        foreach (var (k, v) in after.StoryCounts)
        {
            if (before.StoryCounts.TryGetValue(k, out var old) && old == v) continue;
            if (StoryCounts.TryGetValue(k, out var have) && have >= v) continue;
            StoryCounts[k] = v;
            changed = true;
        }
        return changed;
    }

    static bool AddNew(HashSet<string> target, HashSet<string> before, HashSet<string> after)
    {
        var changed = false;
        foreach (var x in after)
            if (!before.Contains(x) && target.Add(x)) changed = true;
        return changed;
    }

    // Combines a whole profile into this state (used when the server starts, to build a group's ledger).
    public void Merge(State other, bool story)
    {
        foreach (var (id, v) in other.Counters)
        {
            if (!Counters.TryGetValue(id, out var have) || v > have) Counters[id] = v;
            if (other.CounterMeta.TryGetValue(id, out var meta)) CounterMeta[id] = meta;
        }
        foreach (var (q, set) in other.Conditions)
        {
            if (!Conditions.TryGetValue(q, out var mine)) Conditions[q] = mine = new HashSet<string>();
            mine.UnionWith(set);
        }
        Finishable.UnionWith(other.Finishable);
        if (!story) return;
        foreach (var (k, v) in other.Variables)
            if (!Variables.TryGetValue(k, out var have) || v > have) Variables[k] = v;
        Dialogue.UnionWith(other.Dialogue);
        StoryRead.UnionWith(other.StoryRead);
        StoryDone.UnionWith(other.StoryDone);
        StorySkipped.UnionWith(other.StorySkipped);
        foreach (var (k, v) in other.StoryCounts)
            if (!StoryCounts.TryGetValue(k, out var have) || v > have) StoryCounts[k] = v;
    }

    // Forgets a quest's shared progress (when someone restarts a failed quest).
    public void ResetQuest(string questId)
    {
        Conditions.Remove(questId);
        Finishable.Remove(questId);
        foreach (var id in CounterMeta.Where(kv => kv.Value.SourceId == questId).Select(kv => kv.Key).ToList())
        {
            Counters.Remove(id);
            CounterMeta.Remove(id);
        }
    }

    //====================[ Writing to a profile ]====================
    // Brings one profile up to this ledger. Only touches quests the player has started, so it never
    // unlocks or starts quests by itself. Returns a short description of what changed, or null.
    public string? ApplyTo(SptProfile profile, bool story, double now)
    {
        var pmc = profile.CharacterData?.PmcData;
        if (pmc is null) return null;
        var counters = 0;
        var conditions = 0;
        var finishable = 0;

        var quests = new Dictionary<string, QuestStatus>();
        foreach (var quest in pmc.Quests ?? []) quests[quest.QId.ToString()] = quest;

        pmc.TaskConditionCounters ??= new();
        foreach (var (id, value) in Counters)
        {
            if (!CounterMeta.TryGetValue(id, out var meta)) continue;
            if (!quests.TryGetValue(meta.SourceId, out var quest) || !IsActive(quest.Status)) continue;
            var key = new SPTarkov.Server.Core.Models.Common.MongoId(id);
            if (pmc.TaskConditionCounters.TryGetValue(key, out var existing))
            {
                if ((existing.Value ?? 0) >= value) continue;
                existing.Value = value;
            }
            else
            {
                pmc.TaskConditionCounters[key] = new TaskConditionCounter
                {
                    Id = key,
                    Type = meta.Type,
                    SourceId = new SPTarkov.Server.Core.Models.Common.MongoId(meta.SourceId),
                    Value = value,
                };
            }
            counters++;
        }

        foreach (var (q, set) in Conditions)
        {
            if (!quests.TryGetValue(q, out var quest) || !IsActive(quest.Status)) continue;
            quest.CompletedConditions ??= [];
            foreach (var c in set)
            {
                if (quest.CompletedConditions.Contains(c)) continue;
                quest.CompletedConditions.Add(c);
                conditions++;
            }
        }

        foreach (var q in Finishable)
        {
            if (!quests.TryGetValue(q, out var quest) || quest.Status != QuestStatusEnum.Started) continue;
            quest.Status = QuestStatusEnum.AvailableForFinish;
            quest.StatusTimers ??= new();
            quest.StatusTimers[QuestStatusEnum.AvailableForFinish] = now;
            finishable++;
        }

        var storyChanges = 0;
        if (story) storyChanges = ApplyStory(profile, pmc);

        if (counters + conditions + finishable + storyChanges == 0) return null;
        return $"{counters} counter(s), {conditions} step(s), {finishable} ready to turn in, {storyChanges} story change(s)";
    }

    int ApplyStory(SptProfile profile, PmcData pmc)
    {
        var changes = 0;
        pmc.Variables ??= new();
        foreach (var (k, v) in Variables)
        {
            var key = new SPTarkov.Server.Core.Models.Common.MongoId(k);
            if (pmc.Variables.TryGetValue(key, out var have) && have == v) continue;
            pmc.Variables[key] = v;
            changes++;
        }

        profile.DialogueProgress ??= [];
        var have2 = new HashSet<string>(profile.DialogueProgress.Select(DialogueKey));
        foreach (var key in Dialogue)
        {
            if (have2.Contains(key)) continue;
            var parts = key.Split('|');
            if (parts.Length != 3) continue;
            profile.DialogueProgress.Add(new NodePathTraveled { TraderId = parts[0], DialogueId = parts[1], NodeId = parts[2] });
            changes++;
        }

        if (StoryRead.Count + StoryDone.Count + StorySkipped.Count + StoryCounts.Count == 0) return changes;

        var visit = ReadVisit(profile) ?? new JsonObject();
        var visitChanges = 0;
        visitChanges += UnionInto(visit, "read", StoryRead);
        if (visit["objectives"] is not JsonObject objectives)
        {
            objectives = new JsonObject();
            visit["objectives"] = objectives;
        }
        visitChanges += UnionInto(objectives, "done", StoryDone);
        visitChanges += UnionInto(objectives, "skipped", StorySkipped);
        if (objectives["counts"] is not JsonObject counts)
        {
            counts = new JsonObject();
            objectives["counts"] = counts;
        }
        foreach (var (k, v) in StoryCounts)
        {
            if (counts[k] is JsonValue jv && jv.TryGetValue<double>(out var have3) && have3 >= v) continue;
            counts[k] = v;
            visitChanges++;
        }

        if (visitChanges > 0)
        {
            profile.ExtensionData ??= new();
            profile.ExtensionData[VisitKey] = JsonSerializer.SerializeToElement(visit);
        }
        return changes + visitChanges;
    }

    static int UnionInto(JsonObject parent, string name, HashSet<string> values)
    {
        if (parent[name] is not JsonArray arr)
        {
            arr = new JsonArray();
            parent[name] = arr;
        }
        var have = new HashSet<string>();
        foreach (var x in arr)
            if (x is JsonValue v && v.TryGetValue<string>(out var s)) have.Add(s);
        var added = 0;
        foreach (var v in values)
        {
            if (have.Contains(v)) continue;
            arr.Add(v);
            added++;
        }
        return added;
    }
}
