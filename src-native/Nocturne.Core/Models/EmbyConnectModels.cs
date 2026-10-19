using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

public class PinCreationResult
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Pin")]
    public string Pin { get; set; } = string.Empty;

    [JsonPropertyName("DeviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("IsExpired")]
    public bool IsExpired { get; set; }

    [JsonPropertyName("IsConfirmed")]
    public bool IsConfirmed { get; set; }

    [JsonPropertyName("AccessToken")]
    public string? AccessToken { get; set; }
}

public class PinExchangeResult
{
    [JsonPropertyName("AccessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("UserId")]
    public string UserId { get; set; } = string.Empty;
}

public class ConnectServer
{
    [JsonPropertyName("AccessKey")]
    public string AccessKey { get; set; } = string.Empty;

    [JsonPropertyName("SystemId")]
    public string SystemId { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Url")]
    public string? Url { get; set; }

    [JsonPropertyName("LocalAddress")]
    public string? LocalAddress { get; set; }

    [JsonPropertyName("UserType")]
    public string? UserType { get; set; }
}

public class ConnectExchangeResponse
{
    [JsonPropertyName("LocalUserId")]
    public string? LocalUserId { get; set; }

    [JsonPropertyName("AccessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("UserId")]
    public string? UserId { get; set; }
}
