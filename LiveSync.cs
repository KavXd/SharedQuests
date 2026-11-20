// The address the optional Shared Quests Live client plugin talks to.
//
// While a player is in the menus, the plugin sends what their game currently has (quest statuses,
// finished steps and counters) to /sharedquests/sync every few seconds. The server:
//   1. records that as the player's "client view", so the end-of-raid sums stay right (a friend's
//      progress the game was given is not counted again as the player's own), and
//   2. answers with the player's quests as the server has them, including progress from the group.
// The plugin then puts any newer progress into the game and reports again.
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SharedQuests;

public sealed class QuestReport
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("conditions")] public List<string>? Conditions { get; set; }
}

public sealed class CounterReport
{
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("quest")] public string Quest { get; set; } = "";
}

public record SyncRequest : IRequestData
{
    // Counter values the game has, by counter id.
    [JsonPropertyName("counters")] public Dictionary<string, double>? Counters { get; set; }
    // Quests the game has, by quest id.
    [JsonPropertyName("quests")] public Dictionary<string, QuestReport>? Quests { get; set; }
}

public sealed class SyncReply
{
    // False when this player isn't in a group (or live sync is off): the plugin stops asking for a while.
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("counters")] public Dictionary<string, CounterReport> Counters { get; set; } = new();
    [JsonPropertyName("quests")] public Dictionary<string, QuestReport> Quests { get; set; } = new();
}

[Injectable]
public class LiveSyncRouter(JsonUtil jsonUtil)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<SyncRequest>(
                "/sharedquests/sync",
                (url, request, sessionId, output, cancellationToken) =>
                    new ValueTask<string>(
                        jsonUtil.Serialize(SharedQuestService.Instance?.ClientSync(sessionId, request) ?? new SyncReply()) ?? "{}"
                    )
            ),
        ]
    );
