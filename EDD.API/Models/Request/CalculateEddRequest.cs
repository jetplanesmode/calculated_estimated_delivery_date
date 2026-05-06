namespace EDD.API.Models.Request;

public sealed record CalculateEddRequest(
    string Origin,
    string Destination,
    string Mode,
    string ServiceType,
    DateTime PickupDate,
    /// <summary>ISO country code used to filter holidays; omit to skip holiday table.</summary>
    string? Country = null,
    string? Carrier = null,
    bool IsRural = false,
    string? FreightPayer = null);
