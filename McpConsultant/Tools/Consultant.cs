using System.ComponentModel;

using McpCommon.Exceptions;
using McpCommon.Services;
using McpConsultant.Models;
using McpConsultant.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpConsultant.Tools;

/// <summary>
/// The consult MCP tool. Class name deliberately differs from the method name
/// (C# forbids a member sharing its enclosing type's name).
/// </summary>
internal class Consultant(
    OpenRouterClient openRouterClient,
    VpnDetectionService vpnDetectionService,
    IOptions<ConsultantSettings> settingsOptions,
    ILogger<Consultant> logger)
{
    private const string VpnFailureMessage =
        "The user must connect to Mullvad VPN before consultations can be performed. " +
        "Please inform the user and wait for them to connect before retrying the consultation.";

    [McpServerTool]
    [Description(
        "Consult an external expert model for a second opinion on a spec, design, or hard problem. " +
        "Use for judgment calls worth an independent perspective, not routine lookups. " +
        "Pass supporting material as file paths via 'files' (the server reads them; do not inline large content).")]
    public async Task<string> Consult(
        [Description("The question you want the consultant's opinion on")] string question,
        [Description("Optional inline supporting material: excerpts, prior analysis, constraints")] string? context = null,
        [Description("Optional fully qualified paths to UTF-8 text files to attach")] string[]? files = null,
        [Description("Optional OpenRouter model ID overriding the configured default")] string? model = null,
        [Description("Optional reasoning effort: low, high, or max (default: high)")] string? effort = null,
        CancellationToken cancellationToken = default)
    {
        // Correlates this call's lifecycle lines on stderr, so overlapping consultations stay pairable.
        var consultationId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var settings = settingsOptions.Value;

            // Step 1: question
            if (string.IsNullOrWhiteSpace(question))
            {
                return LogAndReturn(consultationId, ErrorMessages.EmptyQuestion());
            }

            // Step 2: effort resolution and membership
            var resolvedEffort = string.IsNullOrWhiteSpace(effort) ? settings.DefaultEffort : effort;
            var normalizedEffort = EffortLevels.Allowed.FirstOrDefault(
                e => string.Equals(e, resolvedEffort?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (normalizedEffort is null)
            {
                return LogAndReturn(consultationId, ErrorMessages.InvalidEffort(resolvedEffort ?? "(null)"));
            }

            // Step 3: model resolution
            var resolvedModel = (string.IsNullOrWhiteSpace(model) ? settings.DefaultModel : model).Trim();
            if (string.IsNullOrWhiteSpace(resolvedModel))
            {
                return LogAndReturn(consultationId, ErrorMessages.MissingDefaultModel());
            }

            // Step 4: numeric settings positivity
            if (settings.TimeoutSeconds <= 0)
            {
                return LogAndReturn(consultationId, ErrorMessages.NonPositiveSetting(nameof(settings.TimeoutSeconds), settings.TimeoutSeconds));
            }

            if (settings.MaxAttachmentBytes <= 0)
            {
                return LogAndReturn(consultationId, ErrorMessages.NonPositiveSetting(nameof(settings.MaxAttachmentBytes), settings.MaxAttachmentBytes));
            }

            // Step 5: API key presence (blank counts as absent)
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return LogAndReturn(consultationId, ErrorMessages.MissingApiKey());
            }

            // Step 6: VPN, only when required
            if (settings.RequireVpn)
            {
                vpnDetectionService.EnsureVpnConnected(VpnFailureMessage);
            }

            // Step 7: file validation (metadata and budget before any content read)
            var validation = FileAttachmentValidator.Validate(files, settings.MaxAttachmentBytes);
            if (validation.Error is not null)
            {
                return LogAndReturn(consultationId, validation.Error);
            }

            // Step 8: the request
            var systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
                ? PromptAssembler.DefaultSystemPrompt
                : settings.SystemPrompt;
            var userMessage = PromptAssembler.BuildUserMessage(question, context, validation.Files);

            logger.LogInformation("Consultation started [{ConsultationId}]: {Model} (effort: {Effort})", consultationId, resolvedModel, normalizedEffort);

            var result = await openRouterClient.ConsultAsync(
                new OpenRouterCall
                {
                    ApiKey = settings.ApiKey,
                    Model = resolvedModel,
                    Effort = normalizedEffort,
                    SystemPrompt = systemPrompt,
                    UserMessage = userMessage,
                    Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
                },
                cancellationToken);

            if (result.Usage is not null)
            {
                logger.LogInformation(
                    "Consultation completed [{ConsultationId}]: prompt={PromptTokens} completion={CompletionTokens} reasoning={ReasoningTokens}",
                    consultationId,
                    result.Usage.PromptTokens,
                    result.Usage.CompletionTokens,
                    result.Usage.ReasoningTokens?.ToString() ?? "n/a");
            }
            else
            {
                logger.LogInformation("Consultation completed [{ConsultationId}]: no usage reported", consultationId);
            }

            return ResponseFormatter.FormatSuccess(resolvedModel, normalizedEffort, result.FinishReason, result.Answer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client's own token fired: no listener exists, so no structured error.
            logger.LogInformation("Consultation cancelled [{ConsultationId}]", consultationId);

            throw;
        }
        catch (VpnNotConnectedException ex)
        {
            return LogAndReturn(consultationId, ErrorMessages.VpnNotConnected(ex.Message));
        }
        catch (ConsultationTimeoutException ex)
        {
            return LogAndReturn(consultationId, ErrorMessages.Timeout((int)ex.Timeout.TotalSeconds));
        }
        catch (OpenRouterApiException ex) when (ex.StatusCode is not null)
        {
            return LogAndReturn(consultationId, ErrorMessages.HttpNonSuccess(ex.StatusCode.Value, ex.BodyExcerpt));
        }
        catch (OpenRouterApiException ex)
        {
            return LogAndReturn(consultationId, ErrorMessages.UnusableResponse(ex.BodyExcerpt));
        }
        catch (HttpRequestException ex)
        {
            return LogAndReturn(consultationId, ErrorMessages.NetworkFailure(ex.Message));
        }
        catch (Exception ex)
        {
            // Residual class: anything not mapped above, including lazy options-binding failures.
            return LogAndReturn(consultationId, ErrorMessages.UnexpectedError(ex.Message));
        }
    }

    private string LogAndReturn(string consultationId, string error)
    {
        logger.LogError("Consultation failed [{ConsultationId}]: {Error}", consultationId, error);

        return error;
    }
}
