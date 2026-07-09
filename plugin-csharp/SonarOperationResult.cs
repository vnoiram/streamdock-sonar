namespace StreamDockSonar;

public sealed record SonarOperationResult(
    bool Success,
    string? Mode,
    string? Route,
    int? StatusCode,
    string? ErrorSummary)
{
    public static SonarOperationResult Ok(string? mode, string? route, int? statusCode = null)
    {
        return new SonarOperationResult(true, mode, route, statusCode, null);
    }

    public static SonarOperationResult Error(string? mode, string? route, int? statusCode, string errorSummary)
    {
        return new SonarOperationResult(false, mode, route, statusCode, errorSummary);
    }
}
