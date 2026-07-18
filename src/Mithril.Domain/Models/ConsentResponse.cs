namespace Mithril.Domain.Models;

public class ConsentResponse
{
    public bool Approved { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? TokenUrl { get; set; }
}
