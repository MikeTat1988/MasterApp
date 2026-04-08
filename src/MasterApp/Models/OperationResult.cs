namespace MasterApp.Models;

public sealed class OperationResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;

    public static OperationResult Success(string message) => new() { Ok = true, Message = message };
    public static OperationResult Failure(string message) => new() { Ok = false, Message = message };
}
