namespace EDD.API.Models;

public class TransitTime
{
    public int Id { get; set; }
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Mode { get; set; } = "";
    public string ServiceType { get; set; } = "";
    /// <summary>Working days of transit (applied with weekend/holiday-aware calendar).</summary>
    public int TransitDays { get; set; }
}
