using System.Text.Json;
using EDD.API.Models.Dtos;

namespace EDD.API.Models;

/// <summary>Business rules for EDD; parameters live in <see cref="RuleJson"/>.</summary>
public class EddRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public bool IsActive { get; set; }
    public string RuleType { get; set; } = "";
    /// <summary>JSON (jsonb): <see cref="RuleDefinition"/> — <c>conditions</c> + <c>actions</c> (see <c>RuleEngine</c>).</summary>
    public JsonElement RuleJson { get; set; }
    public string Version { get; set; } = "1";
    public DateTime CreatedAt { get; set; }
}
