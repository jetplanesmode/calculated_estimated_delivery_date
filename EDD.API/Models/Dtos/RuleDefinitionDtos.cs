using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDD.API.Models.Dtos;

/// <summary>Stored in <c>edd_rules.rule_json</c> for data-driven rules (conditions + actions).</summary>
public sealed class RuleDefinition
{
    [JsonPropertyName("conditions")]
    public List<Condition>? Conditions { get; set; }

    [JsonPropertyName("actions")]
    public List<ActionRule>? Actions { get; set; }
}

public sealed class Condition
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    /// <summary>JSON key <c>operator</c> (equals, greater_than, in, …).</summary>
    [JsonPropertyName("operator")]
    public string Op { get; set; } = "";

    /// <summary>Scalar or JSON array (for <c>in</c>). Omit if not needed.</summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

public sealed class ActionRule
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("value")]
    public int Value { get; set; }
}
