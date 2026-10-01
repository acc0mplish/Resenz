using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resenz.Core.Services;

public sealed record ElevatedRequest(
    string Command,
    string? WorkingDirectory = null,
    string? ConfigPath = null,
    string? BinaryPath = null,
    string? Secret = null);

public sealed record ElevatedResponse(
    bool Success,
    string? Message = null,
    int? ProcessId = null,
    string? Version = null);

public static class ElevatedProtocol
{
    public const string PipeName = "Resenz.Elevated";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string Start = "start";
    public const string Stop = "stop";
    public const string Status = "status";
    public const string Shutdown = "shutdown";
}
