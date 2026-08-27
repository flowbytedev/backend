using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Shared.Models.Data.Pipelines;
using Application.Shared.Options;
using Microsoft.Extensions.Options;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Sends one pipeline export through the Next.js/Resend email service — the same service, and the same
/// POST-a-payload shape, as the incident, sales-snapshot and dataset-shared emails.
/// <para>
/// Unlike those three, this one <b>returns its failures instead of swallowing them</b>. A notification that
/// silently does not arrive is a nuisance; a scheduled export that silently does not arrive is a report
/// somebody is waiting on, and a run that goes green having delivered nothing is the worst possible
/// outcome. So every failure comes back as a step failure.
/// </para>
/// </summary>
public interface IPipelineEmailSender
{
    Task<EmailSendResult> SendAsync(EmailSendRequest request, CancellationToken ct = default);

    /// <summary>Whether this deployment can send at all, so the engine can fail early with a clear reason.</summary>
    bool IsConfigured { get; }

    /// <summary>The attachment ceiling in bytes, so the caller can decide before building a file.</summary>
    long MaxAttachmentBytes { get; }

    /// <summary>The row ceiling for one export.</summary>
    int MaxRows { get; }

    /// <summary>Public base URL of the app, for building the dataset link. Null when not configured.</summary>
    string? AppBaseUri { get; }
}

public sealed class EmailSendRequest
{
    public required IReadOnlyList<string> To { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public IReadOnlyList<string>? Bcc { get; init; }
    public IReadOnlyList<string>? ReplyTo { get; init; }

    public required string Subject { get; init; }

    /// <summary>Free text shown above the summary. Rendered as paragraphs, never as HTML.</summary>
    public string? Message { get; init; }

    public required string PipelineName { get; init; }
    public string? RunId { get; init; }

    /// <summary>Rows in the export. Shown in the body, and 0 is a fact worth stating rather than hiding.</summary>
    public long Rows { get; init; }

    /// <summary>Absolute path of the file to attach. Null sends a body-only mail.</summary>
    public string? AttachmentPath { get; init; }

    /// <summary>Name the recipient sees. Defaults to the file's own name.</summary>
    public string? AttachmentName { get; init; }

    /// <summary>
    /// Set instead of an attachment when the export was too large: a link to the dataset table the rows were
    /// written into, plus the label to show for it.
    /// </summary>
    public string? LinkUrl { get; init; }
    public string? LinkLabel { get; init; }

    /// <summary>Why there is a link rather than a file. Shown as a note, so the recipient is not left guessing.</summary>
    public string? LinkReason { get; init; }
}

public sealed record EmailSendResult(bool Success, string? Error, string? ErrorType, long AttachmentBytes)
{
    public static EmailSendResult Ok(long bytes) => new(true, null, null, bytes);

    public static EmailSendResult Fail(string error, string errorType) =>
        new(false, error, errorType, 0);
}

public class PipelineEmailSender(
    IHttpClientFactory httpClientFactory,
    IOptions<PipelineEmailOptions> options) : IPipelineEmailSender
{
    /// <summary>Registered in both hosts' Program.cs with the base address from the options above.</summary>
    public const string HttpClientName = "PipelineExportEmailApi";

    private readonly PipelineEmailOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public long MaxAttachmentBytes => _options.ResolveMaxAttachmentBytes();

    public int MaxRows => _options.ResolveMaxRows();

    public string? AppBaseUri => _options.AppBaseUri;

    public async Task<EmailSendResult> SendAsync(EmailSendRequest request, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            return EmailSendResult.Fail(
                "Sending email is not configured on this server. Set PipelineEmail:ApiBaseUri and "
                + "PipelineEmail:From in appsettings.",
                PipelineErrorType.NotWritable);
        }

        var to = Clean(request.To);
        if (to.Count == 0)
            return EmailSendResult.Fail("This step has no valid recipient.", PipelineErrorType.Invalid);

        string? content = null;
        long bytes = 0;

        if (!string.IsNullOrWhiteSpace(request.AttachmentPath))
        {
            if (!File.Exists(request.AttachmentPath))
            {
                return EmailSendResult.Fail(
                    $"The export file '{request.AttachmentPath}' is missing.", PipelineErrorType.Unknown);
            }

            bytes = new FileInfo(request.AttachmentPath!).Length;

            // Re-checked here even though the engine already checked: this is the last point before the
            // bytes are turned into a base64 string in memory, and that allocation is ~1.33x the file.
            if (bytes > MaxAttachmentBytes)
            {
                return EmailSendResult.Fail(
                    $"The export is {Mb(bytes)}, over the {Mb(MaxAttachmentBytes)} attachment limit.",
                    PipelineErrorType.Invalid);
            }

            content = Convert.ToBase64String(await File.ReadAllBytesAsync(request.AttachmentPath!, ct));
        }

        var payload = new PipelineExportEmailPayload
        {
            From = _options.From!,
            To = to,
            Cc = Optional(request.Cc),
            Bcc = Optional(request.Bcc),
            ReplyTo = Optional(request.ReplyTo),
            Subject = request.Subject,
            Message = request.Message,
            PipelineName = request.PipelineName,
            RunId = request.RunId,
            RowCount = request.Rows,
            FileName = content is null ? null : AttachmentName(request),
            FileSizeLabel = content is null ? null : Mb(bytes),
            LinkUrl = request.LinkUrl,
            LinkLabel = request.LinkLabel,
            LinkReason = request.LinkReason,
            Attachments = content is null
                ? null
                : [new EmailAttachment { Filename = AttachmentName(request), Content = content }]
        };

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var response = await client.PostAsJsonAsync(_options.Endpoint, payload, ct);

            if (!response.IsSuccessStatusCode)
            {
                // The route answers with a reason; carrying it through is the difference between "the email
                // failed" and "the from address is not a verified domain".
                var body = await SafeBodyAsync(response, ct);
                return EmailSendResult.Fail(
                    $"The email service refused the message ({(int)response.StatusCode})"
                    + (string.IsNullOrWhiteSpace(body) ? "." : $": {body}"),
                    PipelineErrorType.ApiError);
            }

            return EmailSendResult.Ok(bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return EmailSendResult.Fail(
                $"The email service did not respond within {_options.ResolveTimeoutSeconds()}s.",
                PipelineErrorType.ApiError);
        }
        catch (Exception ex)
        {
            return EmailSendResult.Fail(
                $"Could not reach the email service: {ex.Message}", PipelineErrorType.ApiError);
        }
    }

    private static string AttachmentName(EmailSendRequest request) =>
        string.IsNullOrWhiteSpace(request.AttachmentName)
            ? Path.GetFileName(request.AttachmentPath!)
            : request.AttachmentName!;

    private static async Task<string?> SafeBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Length > 400 ? text[..400] : text;
        }
        catch { return null; }
    }

    /// <summary>
    /// Splits on comma, semicolon and newline, so a recipients field can be filled in whichever way comes
    /// naturally, and drops anything without an <c>@</c> rather than handing the provider a value it will
    /// reject for the whole message.
    /// </summary>
    internal static List<string> Clean(IEnumerable<string>? addresses)
    {
        if (addresses is null) return [];

        return addresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .SelectMany(a => a.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && a.Contains('@') && !a.StartsWith('@') && !a.EndsWith('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string>? Optional(IEnumerable<string>? addresses)
    {
        var cleaned = Clean(addresses);
        return cleaned.Count == 0 ? null : cleaned;
    }

    internal static string Mb(long bytes) =>
        bytes < 1024L * 1024L
            ? $"{bytes / 1024.0:N0} KB"
            : $"{bytes / (1024.0 * 1024.0):N1} MB";

    /// <summary>
    /// The JSON body the <c>/api/email/pipeline-export</c> route expects. Property names are explicit
    /// because the route reads camelCase and a rename on either side must be a deliberate edit to both.
    /// </summary>
    private sealed class PipelineExportEmailPayload
    {
        [JsonPropertyName("from")] public string From { get; set; } = string.Empty;
        [JsonPropertyName("to")] public List<string> To { get; set; } = [];
        [JsonPropertyName("cc")] public List<string>? Cc { get; set; }
        [JsonPropertyName("bcc")] public List<string>? Bcc { get; set; }
        [JsonPropertyName("replyTo")] public List<string>? ReplyTo { get; set; }
        [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("pipelineName")] public string PipelineName { get; set; } = string.Empty;
        [JsonPropertyName("runId")] public string? RunId { get; set; }
        [JsonPropertyName("rowCount")] public long RowCount { get; set; }
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
        [JsonPropertyName("fileSizeLabel")] public string? FileSizeLabel { get; set; }
        [JsonPropertyName("linkUrl")] public string? LinkUrl { get; set; }
        [JsonPropertyName("linkLabel")] public string? LinkLabel { get; set; }
        [JsonPropertyName("linkReason")] public string? LinkReason { get; set; }
        [JsonPropertyName("attachments")] public List<EmailAttachment>? Attachments { get; set; }
    }

    private sealed class EmailAttachment
    {
        [JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;

        /// <summary>Base64. Resend's own attachment shape, passed straight through by the route.</summary>
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }
}
