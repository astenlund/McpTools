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
    private static readonly string[] AllowedEfforts = ["low", "high", "max"];

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
        try
        {
            var settings = settingsOptions.Value;

            // Step 1: question
            if (string.IsNullOrWhiteSpace(question))
            {
                return ErrorMessages.EmptyQuestion();
            }

            // Step 2: effort resolution and membership
            var resolvedEffort = string.IsNullOrWhiteSpace(effort) ? settings.DefaultEffort : effort;
            var normalizedEffort = AllowedEfforts.FirstOrDefault(
                e => string.Equals(e, resolvedEffort?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (normalizedEffort is null)
            {
                return ErrorMessages.InvalidEffort(resolvedEffort ?? "(null)");
            }

            // Step 3: model resolution
            var resolvedModel = string.IsNullOrWhiteSpace(model) ? settings.DefaultModel : model.Trim();
            if (string.IsNullOrWhiteSpace(resolvedModel))
            {
                return ErrorMessages.MissingDefaultModel();
            }

            // Step 4: numeric settings positivity
            if (settings.TimeoutSeconds <= 0)
            {
                return ErrorMessages.NonPositiveSetting(nameof(settings.TimeoutSeconds), settings.TimeoutSeconds);
            }

            if (settings.MaxAttachmentBytes <= 0)
            {
                return ErrorMessages.NonPositiveSetting(nameof(settings.MaxAttachmentBytes), settings.MaxAttachmentBytes);
            }

            // Step 5: API key presence (blank counts as absent)
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return ErrorMessages.MissingApiKey();
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
                return validation.Error;
            }

            // Step 8: the request
            var systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
                ? PromptAssembler.DefaultSystemPrompt
                : settings.SystemPrompt;
            var userMessage = PromptAssembler.BuildUserMessage(question, context, validation.Files);

            logger.LogInformation("Consultation started: {Model} (effort: {Effort})", resolvedModel, normalizedEffort);

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
                    "Consultation completed: prompt={PromptTokens} completion={CompletionTokens} reasoning={ReasoningTokens}",
                    result.Usage.PromptTokens,
                    result.Usage.CompletionTokens,
                    result.Usage.ReasoningTokens?.ToString() ?? "n/a");
            }
            else
            {
                logger.LogInformation("Consultation completed: no usage reported");
            }

            return ResponseFormatter.FormatSuccess(resolvedModel, normalizedEffort, result.FinishReason, result.Answer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client's own token fired: no listener exists, so no structured error.
            logger.LogInformation("Consultation cancelled");

            throw;
        }
        catch (VpnNotConnectedException ex)
        {
            return LogAndReturn(ErrorMessages.VpnNotConnected(ex.Message));
        }
        catch (ConsultationTimeoutException ex)
        {
            return LogAndReturn(ErrorMessages.Timeout((int)ex.Timeout.TotalSeconds));
        }
        catch (OpenRouterApiException ex) when (ex.StatusCode is not null)
        {
            return LogAndReturn(ErrorMessages.HttpNonSuccess(ex.StatusCode.Value, ex.BodyExcerpt));
        }
        catch (OpenRouterApiException ex)
        {
            return LogAndReturn(ErrorMessages.UnusableResponse(ex.BodyExcerpt));
        }
        catch (HttpRequestException ex)
        {
            return LogAndReturn(ErrorMessages.NetworkFailure(ex.Message));
        }
        catch (Exception ex)
        {
            // Residual class: anything not mapped above, including lazy options-binding failures.
            return LogAndReturn(ErrorMessages.UnexpectedError(ex.Message));
        }
    }

    private string LogAndReturn(string error)
    {
        logger.LogError("{Error}", error);

        return error;
    }
}
