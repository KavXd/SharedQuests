using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedQuests;

public sealed class SyncConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    // Each group is a list of players who share quests. A player can be named by profile id, by launcher
    // username, or by in-game nickname. A player should only be in one group.
    [JsonPropertyName("groups")] public List<List<string>> Groups { get; set; } = [];

    // Share the VisitAPI story (EFT 1.2 Story Backport): story variables, dialogue choices, story objectives.
    [JsonPropertyName("syncStory")] public bool SyncStory { get; set; } = true;

    // Give everyone in a group the same dailies and weeklies.
    [JsonPropertyName("syncDailiesAndWeeklies")] public bool SyncRepeatables { get; set; } = true;

    // When one player accepts a quest, accept it for the others too (if they have it unlocked).
    [JsonPropertyName("shareQuestAccept")] public bool ShareAccept { get; set; } = true;

    // How kills and other counters add up when group members play the same Fika raid together.
    //   "auto": read Fika's sharedQuestProgression setting. On: Fika already shares those kills, so use "max".
    //   "max":  each counter goes up by the largest gain any of them made in that raid.
    //   "sum":  gains from everyone in the raid are added together.
    [JsonPropertyName("sameRaidCounting")] public string SameRaidCounting { get; set; } = "auto";

    // Seconds between checks for changes made in menus (hand-ins, story choices, ...).
    [JsonPropertyName("checkIntervalSeconds")] public int CheckIntervalSeconds { get; set; } = 2;

    [JsonPropertyName("logChanges")] public bool LogChanges { get; set; } = true;

    public static SyncConfig Load(string path)
    {
        if (!File.Exists(path)) return new SyncConfig();
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(path), options) ?? new SyncConfig();
    }
}
