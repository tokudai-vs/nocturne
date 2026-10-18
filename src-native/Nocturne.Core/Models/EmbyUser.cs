namespace Nocturne.Core.Models;

public class EmbyUser
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PrimaryImageTag { get; set; }
    public bool HasPassword { get; set; }
    public UserPolicy? Policy { get; set; }
}

public class UserPolicy
{
    public bool IsAdministrator { get; set; }
}

public class PublicUser
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PrimaryImageTag { get; set; }
    public bool HasPassword { get; set; }
}

public class AuthResult
{
    public EmbyUser? User { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
}

public class EmbyServerInfo
{
    public string? Id { get; set; }
    public string? ServerName { get; set; }
    public string? Version { get; set; }
    public string? OperatingSystem { get; set; }
}
