namespace EDD.API.Models;

public class EddLog
{
    public Guid Id { get; set; }
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Mode { get; set; } = "";
    public string ServiceType { get; set; } = "";
    public DateTime PickupDate { get; set; }
    public DateTime CalculatedEdd { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Rule names and non-working-day trace (same as API <c>appliedRules</c>).</summary>
    public List<string> AppliedRules { get; set; } = [];
    /// <summary>Total non-working calendar days crossed (same as API <c>nonDeliveryDaysSkipped</c>).</summary>
    public int NonDeliveryDaysSkipped { get; set; }
}
