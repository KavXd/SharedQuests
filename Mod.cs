// Shared Quests: players in a group share quest progress, dailies/weeklies and the VisitAPI story.
// Each player still turns quests in themselves and gets their own rewards.
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace SharedQuests;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "akvxxx16.sharedquests";
    public string Name { get; init; } = "Shared Quests";
    public string Author { get; init; } = "AKVXXX16";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}
