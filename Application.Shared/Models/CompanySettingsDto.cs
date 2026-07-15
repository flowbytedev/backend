namespace Application.Shared.Models;

/// <summary>Wire shape for the per-company settings API (shared by the server controller and the client).</summary>
public class CompanySettingsDto
{
    public bool DebugLoggingEnabled { get; set; }
}
