namespace Logibooks.Alta.Configuration;

internal sealed class CoreOptions
{
    public const string SectionName = "Core";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int JwtRefreshSkewMinutes { get; set; } = 5;
}
