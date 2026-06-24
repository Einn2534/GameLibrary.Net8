using System.Text;

namespace GameLibrary.Net8;

public sealed class GameIconFetchDiagnostics
{
    public string LastAttemptedSource { get; set; } = string.Empty;

    public string SourcePageUrl { get; set; } = string.Empty;

    public string RequestUrl { get; set; } = string.Empty;

    public int? HttpStatus { get; set; }

    public string SavedError { get; set; } = string.Empty;

    public bool HasFailure =>
        !string.IsNullOrWhiteSpace(SavedError) ||
        HttpStatus >= 400;

    public bool HasSourcePageUrl => !string.IsNullOrWhiteSpace(SourcePageUrl);

    public string HttpStatusText => HttpStatus.HasValue
        ? HttpStatus.Value.ToString()
        : string.Empty;

    public string Summary
    {
        get
        {
            List<string> parts = [];

            if (!string.IsNullOrWhiteSpace(LastAttemptedSource))
            {
                parts.Add("Source: " + LastAttemptedSource);
            }

            if (HttpStatus.HasValue)
            {
                parts.Add("HTTP: " + HttpStatus.Value);
            }

            if (!string.IsNullOrWhiteSpace(SavedError))
            {
                parts.Add("Error: " + SavedError);
            }

            return string.Join(" | ", parts);
        }
    }

    public string ToDiagnosticText(string gameName)
    {
        var builder = new StringBuilder();
        AppendLine(builder, "Game", gameName);
        AppendLine(builder, "Last attempted source", LastAttemptedSource);
        AppendLine(builder, "Source page URL", SourcePageUrl);
        AppendLine(builder, "Request URL", RequestUrl);
        AppendLine(builder, "HTTP status", HttpStatusText);
        AppendLine(builder, "Saved error", SavedError);
        return builder.ToString().TrimEnd();
    }

    private static void AppendLine(StringBuilder builder, string label, string value)
    {
        builder.Append(label);
        builder.Append(": ");
        builder.AppendLine(value ?? string.Empty);
    }
}
