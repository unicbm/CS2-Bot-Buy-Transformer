using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotBuyPatch;

internal enum TeamEconomyClass
{
    Pistol,
    Eco,
    Force,
    Full
}

internal sealed class BuyPolicyDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("economy_thresholds")]
    public EconomyThresholds EconomyThresholds { get; init; } = new();

    [JsonPropertyName("training")]
    public TrainingSummary Training { get; init; } = new();

    [JsonPropertyName("policies")]
    public Dictionary<string, Dictionary<string, EconomyPolicy>> Policies { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static BuyPolicyDocument Load(string path)
    {
        var document = JsonSerializer.Deserialize<BuyPolicyDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("policy JSON deserialized to null");
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"unsupported schema_version {document.SchemaVersion}");
        if (document.Training.DemoCount < 1 || document.Training.PlayerRoundSamples < 1)
            throw new InvalidDataException("policy contains no training evidence");
        return document;
    }

    public EconomyPolicy? Find(string side, TeamEconomyClass economyClass)
    {
        string classKey = economyClass.ToString().ToLowerInvariant();
        return Policies.TryGetValue(side, out var byClass) && byClass.TryGetValue(classKey, out var policy)
            ? policy
            : null;
    }
}

internal sealed class EconomyThresholds
{
    [JsonPropertyName("eco_max_exclusive")]
    public int EcoMaxExclusive { get; init; } = 1400;

    [JsonPropertyName("force_max_exclusive")]
    public int ForceMaxExclusive { get; init; } = 3600;
}

internal sealed class TrainingSummary
{
    [JsonPropertyName("demo_count")]
    public int DemoCount { get; init; }

    [JsonPropertyName("player_round_samples")]
    public int PlayerRoundSamples { get; init; }
}

internal sealed class EconomyPolicy
{
    [JsonPropertyName("samples")]
    public int Samples { get; init; }

    [JsonPropertyName("eligible")]
    public bool Eligible { get; init; }

    [JsonPropertyName("armor_rate")]
    public double ArmorRate { get; init; }

    [JsonPropertyName("helmet_rate")]
    public double HelmetRate { get; init; }

    [JsonPropertyName("defuser_rate")]
    public double DefuserRate { get; init; }

    [JsonPropertyName("primary_choices")]
    public List<WeightedItem> PrimaryChoices { get; init; } = new();

    [JsonPropertyName("secondary_upgrade_rate")]
    public double SecondaryUpgradeRate { get; init; }

    [JsonPropertyName("secondary_upgrades")]
    public List<WeightedItem> SecondaryUpgrades { get; init; } = new();

    [JsonPropertyName("utility_per_player")]
    public Dictionary<string, double> UtilityPerPlayer { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class WeightedItem
{
    [JsonPropertyName("item")]
    public string Item { get; init; } = "";

    [JsonPropertyName("count")]
    public int Count { get; init; }
}
