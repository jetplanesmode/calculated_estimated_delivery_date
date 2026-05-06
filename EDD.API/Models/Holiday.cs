namespace EDD.API.Models;

public class Holiday
{
    public int Id { get; set; }
    public string Country { get; set; } = "";
    public DateOnly HolidayDate { get; set; }
    public string? Description { get; set; }
}
