using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Buddy.Server.Services.Messaging;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public sealed class CopilotClient
{
    private readonly CopilotOptions _options;
    private readonly ILogger<CopilotClient> _logger;
    private readonly ICopilotSessionStore _sessionStore;

    private const string CopilotAllowAllEnvVar = "COPILOT_ALLOW_ALL";

    public CopilotClient(
        IOptions<CopilotOptions> options,
        ILogger<CopilotClient> logger,
        ICopilotSessionStore sessionStore)
    {
        _options = options.Value;
        _logger = logger;
        _sessionStore = sessionStore;
    }

    public async Task<string> StreamCopilotResponseAsync(
        string token,
        string prompt,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken,
        Dictionary<string, string>? context = null,
        string? conversationUserKey = null)
    {
        if (string.IsNullOrWhiteSpace(_options.Cli.Command))
        {
            throw new InvalidOperationException("Copilot CLI command is not configured (Copilot:Cli:Command).");
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            _logger.LogDebug("Copilot token was provided but is ignored in CLI mode; machine-level CLI auth is used.");
        }

        var conversationKey = BuildConversationKey(context, conversationUserKey);

        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            var ephemeralEntry = new CopilotSessionEntry();
            _logger.LogInformation(
                "Copilot request started. SessionId={SessionId}, Mode=ephemeral, ReusePerSession={ReusePerSession}",
                ephemeralEntry.CliSessionId,
                _options.Cli.ReuseProcessPerSession);

            return await StreamWithSessionAsync(ephemeralEntry, prompt, onDelta, cancellationToken, persistConversation: false);
        }

        var entry = await _sessionStore.GetOrCreateAsync(
            conversationKey,
            static _ => Task.FromResult(new CopilotSessionEntry()),
            cancellationToken);

        _logger.LogInformation(
            "Copilot request queued. SessionId={SessionId}, ConversationKey={ConversationKey}, ReusePerSession={ReusePerSession}",
            entry.CliSessionId,
            conversationKey,
            _options.Cli.ReuseProcessPerSession);

        var gateWaitSw = Stopwatch.StartNew();
        await entry.Gate.WaitAsync(cancellationToken);
        gateWaitSw.Stop();

        _logger.LogInformation(
            "Copilot request acquired session gate. SessionId={SessionId}, WaitMs={WaitMs}",
            entry.CliSessionId,
            gateWaitSw.ElapsedMilliseconds);

        var requestSw = Stopwatch.StartNew();
        try
        {
            var response = await StreamWithSessionAsync(entry, prompt, onDelta, cancellationToken, persistConversation: true);

            requestSw.Stop();
            _logger.LogInformation(
                "Copilot request completed. SessionId={SessionId}, DurationMs={DurationMs}, ResponseChars={ResponseChars}",
                entry.CliSessionId,
                requestSw.ElapsedMilliseconds,
                response.Length);

            if (entry.IsFaulted)
            {
                _logger.LogWarning(
                    "Copilot session fault detected; removing session. SessionId={SessionId}, ConversationKey={ConversationKey}",
                    entry.CliSessionId,
                    conversationKey);

                await _sessionStore.RemoveAsync(conversationKey, cancellationToken);
            }

            return response;
        }
        catch (Exception ex)
        {
            requestSw.Stop();
            _logger.LogError(
                ex,
                "Copilot request failed. SessionId={SessionId}, DurationMs={DurationMs}, ConversationKey={ConversationKey}",
                entry.CliSessionId,
                requestSw.ElapsedMilliseconds,
                conversationKey);
            throw;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private string BuildPrompt(string prompt)
    {
        return $"""
Formatting requirements for your final answer:
- Use plain text.
- Use line breaks between sections.
- When summarizing items (emails, messages, files, results), use a bullet list with one item per line.
- Do not include tool traces, internal logs, or execution metadata.

User request:
{prompt}
""";
    }

    private string BuildPromptWithHistory(CopilotSessionEntry entry, string prompt)
    {
        var basePrompt = BuildPrompt(prompt);

        if (entry.ConversationHistory.Count == 0)
        {
            return basePrompt;
        }

        var history = new StringBuilder();
        history.AppendLine("Conversation context from earlier turns:");

        foreach (var turn in entry.ConversationHistory)
        {
            history.AppendLine("User:");
            history.AppendLine(turn.UserPrompt);
            history.AppendLine("Assistant:");
            history.AppendLine(turn.AssistantResponse);
            history.AppendLine();
        }

        history.AppendLine("Current user prompt:");
        history.AppendLine(basePrompt);
        return history.ToString();
    }

    private string? BuildConversationKey(Dictionary<string, string>? context, string? conversationUserKey)
    {
        if (context == null)
        {
            return null;
        }

        if (!context.TryGetValue(MessagingContextKeys.Channel, out var channel) || string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        if (context.TryGetValue(MessagingContextKeys.ThreadTs, out var threadTs) && !string.IsNullOrWhiteSpace(threadTs))
        {
            return $"slack:{channel}:{threadTs}";
        }

        if (!string.IsNullOrWhiteSpace(conversationUserKey))
        {
            return $"slack:{channel}:{conversationUserKey}";
        }

        return null;
    }

    private async Task<string> StreamWithSessionAsync(
        CopilotSessionEntry entry,
        string prompt,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken,
        bool persistConversation)
    {
        var buffer = new StringBuilder();
        var requestState = new CopilotRequestState(buffer, onDelta);
        entry.CurrentRequest = requestState;

        try
        {
            // When using a persistent process, the CLI retains its own conversation context,
            // so we only wrap the prompt with formatting — no history prepend needed.
            // For one-shot or ephemeral sessions, prepend conversation history as before.
            var enrichedPrompt = (_options.Cli.PersistentProcessMode && persistConversation && entry.IsPersistent)
                ? BuildPrompt(prompt)
                : BuildPromptWithHistory(entry, prompt);

            await ExecuteCliRequestAsync(enrichedPrompt, requestState, cancellationToken, persistConversation ? entry : null);

            var response = buffer.ToString();

            if (persistConversation && !string.IsNullOrWhiteSpace(response))
            {
                entry.AddTurn(prompt, response, _options.Cli.MaxConversationTurns);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Copilot CLI streaming");
            entry.MarkFaulted();
            throw;
        }
        finally
        {
            entry.CurrentRequest = null;
            entry.Touch();
        }
    }

    private async Task ExecuteCliRequestAsync(
        string prompt,
        CopilotRequestState requestState,
        CancellationToken cancellationToken,
        CopilotSessionEntry? entry)
    {
        if (entry != null && _options.Cli.PersistentProcessMode)
        {
            try
            {
                await ExecuteCliRequestPersistentAsync(entry, prompt, requestState, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-initiated cancellation — propagate immediately, don't fall back.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Persistent process failed; falling back to one-shot with --resume. SessionId={SessionId}",
                    entry.CliSessionId);

                await DisposePersistentProcessAsync(entry);

                // Reset the request state buffer so the fallback starts clean.
                requestState.Buffer.Clear();
                requestState.ToolUsed = false;

                await ExecuteCliRequestWithResumedSessionAsync(entry, prompt, requestState, cancellationToken);
                return;
            }
        }

        if (entry != null && _options.Cli.ReuseProcessPerSession)
        {
            await ExecuteCliRequestWithResumedSessionAsync(entry, prompt, requestState, cancellationToken);
            return;
        }

        await ExecuteCliRequestOneShotAsync(prompt, requestState, cancellationToken, static _ => { });
    }

    private async Task ExecuteCliRequestWithResumedSessionAsync(
        CopilotSessionEntry entry,
        string prompt,
        CopilotRequestState requestState,
        CancellationToken cancellationToken)
    {
        await ExecuteCliRequestOneShotAsync(
            prompt,
            requestState,
            cancellationToken,
            startInfo =>
            {
                startInfo.ArgumentList.Add("--resume");
                startInfo.ArgumentList.Add(entry.CliSessionId);
                startInfo.ArgumentList.Add("--no-color");
                startInfo.ArgumentList.Add("--no-alt-screen");
            });
    }

    private async Task ExecuteCliRequestPersistentAsync(
        CopilotSessionEntry entry,
        string prompt,
        CopilotRequestState requestState,
        CancellationToken cancellationToken)
    {
        if (entry.CliProcess == null || entry.CliProcess.HasExited)
        {
            await DisposePersistentProcessAsync(entry);
            SpawnPersistentProcess(entry);
        }

        var boundary = $"__BOUNDARY_{Guid.NewGuid():N}__";
        var stdinWriter = entry.StdinWriter
            ?? throw new InvalidOperationException("Persistent process stdin writer is null.");

        _logger.LogInformation(
            "Persistent process: writing prompt. SessionId={SessionId}, Pid={Pid}, PromptChars={PromptChars}, Boundary={Boundary}",
            entry.CliSessionId,
            entry.CliProcess!.Id,
            prompt.Length,
            boundary);

        // Write the real user prompt followed by a sentinel prompt for boundary detection.
        await stdinWriter.WriteLineAsync(prompt.AsMemory(), cancellationToken);
        await stdinWriter.FlushAsync(cancellationToken);

        // Give the CLI a moment to begin processing the real prompt before writing the sentinel.
        // The sentinel is a second prompt that asks the CLI to echo a known boundary marker.
        var sentinelPrompt = $"Reply with exactly: {boundary}";
        await stdinWriter.WriteLineAsync(sentinelPrompt.AsMemory(), cancellationToken);
        await stdinWriter.FlushAsync(cancellationToken);

        // Read stdout line-by-line until the boundary marker appears.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Cli.ResponseTimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.Cli.ResponseTimeoutSeconds));
        }

        var reader = entry.CliProcess.StandardOutput;
        var foundBoundary = false;

        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(timeoutCts.Token);
                if (line == null)
                {
                    // Process exited unexpectedly (stdout closed).
                    throw new InvalidOperationException(
                        $"Persistent Copilot CLI process exited unexpectedly. SessionId={entry.CliSessionId}");
                }

                // Check if this line contains the boundary marker.
                if (line.Contains(boundary, StringComparison.Ordinal))
                {
                    foundBoundary = true;
                    break;
                }

                // Check if this is the sentinel question being echoed back — skip it.
                if (line.Contains(sentinelPrompt, StringComparison.Ordinal))
                {
                    continue;
                }

                await HandleCliOutputLineAsync(line, requestState, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Persistent process response timed out. SessionId={SessionId}, TimeoutSeconds={TimeoutSeconds}",
                entry.CliSessionId,
                _options.Cli.ResponseTimeoutSeconds);
            throw new TimeoutException(
                $"Persistent Copilot CLI response timed out after {_options.Cli.ResponseTimeoutSeconds} seconds.");
        }

        if (!foundBoundary)
        {
            throw new InvalidOperationException(
                $"Persistent Copilot CLI process did not produce boundary marker. SessionId={entry.CliSessionId}");
        }

        var output = requestState.Buffer.ToString();
        _logger.LogInformation(
            "Persistent process: response complete. SessionId={SessionId}, Pid={Pid}, OutputChars={OutputChars}, ToolUsed={ToolUsed}",
            entry.CliSessionId,
            entry.CliProcess.Id,
            output.Length,
            requestState.ToolUsed);
    }

    private void SpawnPersistentProcess(CopilotSessionEntry entry)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Cli.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var arg in _options.Cli.Arguments)
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        startInfo.ArgumentList.Add("--allow-all-tools");
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--no-color");
        startInfo.ArgumentList.Add("--no-alt-screen");

        // Note: No --prompt flag. The process stays alive and reads prompts from stdin.

        ApplyCliEnvironment(startInfo);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Failed to start persistent Copilot CLI process.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Failed to launch persistent Copilot CLI command '{_options.Cli.Command}'. " +
                "Ensure Copilot CLI is installed and available on PATH.", ex);
        }

        entry.CliProcess = process;
        entry.StdinWriter = process.StandardInput;
        entry.StdinWriter.AutoFlush = false;
        entry.IsPersistent = true;

        // Start a background task to drain stderr so it doesn't fill the OS pipe buffer.
        entry.StderrPumpTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logger.LogDebug(
                            "Persistent process stderr: {Line} (SessionId={SessionId}, Pid={Pid})",
                            TruncateForLog(line, 500),
                            entry.CliSessionId,
                            process.Id);
                    }
                }
            }
            catch
            {
                // Process exited or was killed — stop draining.
            }
        });

        _logger.LogInformation(
            "Persistent Copilot CLI process spawned. SessionId={SessionId}, Pid={Pid}, ArgCount={ArgCount}",
            entry.CliSessionId,
            process.Id,
            startInfo.ArgumentList.Count);
    }

    private async Task DisposePersistentProcessAsync(CopilotSessionEntry entry)
    {
        var stdinWriter = entry.StdinWriter;
        entry.StdinWriter = null;

        if (stdinWriter != null)
        {
            try
            {
                stdinWriter.Close();
            }
            catch
            {
                // Ignore.
            }
        }

        var process = entry.CliProcess;
        entry.CliProcess = null;
        entry.IsPersistent = false;

        if (process != null)
        {
            SafeKill(process);
            try
            {
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
            catch
            {
                // Ignore.
            }

            process.Dispose();
        }

        var stderrPump = entry.StderrPumpTask;
        entry.StderrPumpTask = null;

        if (stderrPump != null)
        {
            try
            {
                await stderrPump.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private async Task ExecuteCliRequestOneShotAsync(
        string prompt,
        CopilotRequestState requestState,
        CancellationToken cancellationToken,
        Action<ProcessStartInfo> configureStartInfo)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Cli.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var arg in _options.Cli.Arguments)
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        startInfo.ArgumentList.Add("--allow-all-tools");
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--prompt");
        startInfo.ArgumentList.Add(prompt);
        configureStartInfo(startInfo);

        var requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var mode = TryGetResumeSessionId(startInfo, out var resumeSessionId) ? "resume" : "oneshot";

        ApplyCliEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var execSw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Copilot CLI process starting. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, TimeoutSeconds={TimeoutSeconds}, ArgCount={ArgCount}",
            requestId,
            mode,
            resumeSessionId ?? "-",
            _options.Cli.ResponseTimeoutSeconds,
            startInfo.ArgumentList.Count);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start Copilot CLI process.");
            }

            _logger.LogInformation(
                "Copilot CLI process started. RequestId={RequestId}, Pid={Pid}",
                requestId,
                process.Id);
        }
        catch (Exception ex)
        {
            execSw.Stop();
            _logger.LogError(
                ex,
                "Copilot CLI process failed to start. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, ElapsedMs={ElapsedMs}",
                requestId,
                mode,
                resumeSessionId ?? "-",
                execSw.ElapsedMilliseconds);

            throw new InvalidOperationException($"Failed to launch Copilot CLI command '{_options.Cli.Command}'. Ensure Copilot CLI is installed and available on PATH.", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Cli.ResponseTimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.Cli.ResponseTimeoutSeconds));
        }

        var stdoutTask = PumpStdoutAsync(process, requestState, timeoutCts.Token);
        var stderrTask = ReadAllStderrAsync(process, timeoutCts.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeoutCts.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SafeKill(process);
            execSw.Stop();
            _logger.LogWarning(
                "Copilot CLI process timed out. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, ElapsedMs={ElapsedMs}",
                requestId,
                mode,
                resumeSessionId ?? "-",
                execSw.ElapsedMilliseconds);
            throw new TimeoutException($"Copilot CLI response timed out after {_options.Cli.ResponseTimeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            SafeKill(process);
            execSw.Stop();
            _logger.LogInformation(
                "Copilot CLI process canceled. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, ElapsedMs={ElapsedMs}",
                requestId,
                mode,
                resumeSessionId ?? "-",
                execSw.ElapsedMilliseconds);
            throw;
        }

        execSw.Stop();

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            _logger.LogWarning(
                "Copilot CLI process exited with non-zero code. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, ExitCode={ExitCode}, ElapsedMs={ElapsedMs}, StderrPreview={StderrPreview}",
                requestId,
                mode,
                resumeSessionId ?? "-",
                process.ExitCode,
                execSw.ElapsedMilliseconds,
                TruncateForLog(stderr, 500));

            var message = BuildCliFailureMessage(process.ExitCode, stderr);
            throw new InvalidOperationException(message);
        }

        var output = requestState.Buffer.ToString();
        var stderrFinal = await stderrTask;

        if (string.IsNullOrWhiteSpace(output))
        {
            if (!string.IsNullOrWhiteSpace(stderrFinal))
            {
                _logger.LogWarning(
                    "Copilot CLI returned no output with stderr. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, ElapsedMs={ElapsedMs}, StderrPreview={StderrPreview}",
                    requestId,
                    mode,
                    resumeSessionId ?? "-",
                    execSw.ElapsedMilliseconds,
                    TruncateForLog(stderrFinal, 500));

                throw new InvalidOperationException($"Copilot CLI returned no output. STDERR: {stderrFinal}");
            }

            _logger.LogWarning(
                "Copilot CLI returned no output and no stderr. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, ElapsedMs={ElapsedMs}",
                requestId,
                mode,
                resumeSessionId ?? "-",
                execSw.ElapsedMilliseconds);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(stderrFinal))
            {
                _logger.LogDebug(
                    "Copilot CLI process completed with stderr content. RequestId={RequestId}, StderrPreview={StderrPreview}",
                    requestId,
                    TruncateForLog(stderrFinal, 500));
            }

            _logger.LogInformation(
                "Copilot CLI process completed. RequestId={RequestId}, Mode={Mode}, ResumeSessionId={ResumeSessionId}, ExitCode={ExitCode}, ElapsedMs={ElapsedMs}, OutputChars={OutputChars}, ToolUsed={ToolUsed}, StderrChars={StderrChars}",
                requestId,
                mode,
                resumeSessionId ?? "-",
                process.ExitCode,
                execSw.ElapsedMilliseconds,
                output.Length,
                requestState.ToolUsed,
                stderrFinal.Length);
        }
    }

    private static bool TryGetResumeSessionId(ProcessStartInfo startInfo, out string? sessionId)
    {
        for (var i = 0; i < startInfo.ArgumentList.Count; i++)
        {
            if (!string.Equals(startInfo.ArgumentList[i], "--resume", StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 < startInfo.ArgumentList.Count)
            {
                sessionId = startInfo.ArgumentList[i + 1];
                return true;
            }

            break;
        }

        sessionId = null;
        return false;
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", string.Empty).Replace("\n", " | ").Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "...";
    }

    private async Task PumpStdoutAsync(Process process, CopilotRequestState requestState, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            await HandleCliOutputLineAsync(line, requestState, cancellationToken);
        }
    }

    private async Task HandleCliOutputLineAsync(string line, CopilotRequestState requestState, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var result = TryParseCliEvent(line);

        if (result.ToolUsed)
        {
            requestState.ToolUsed = true;
        }

        if (string.IsNullOrWhiteSpace(result.Content))
        {
            return;
        }

        var content = result.Content;
        if (result.IsThinking)
        {
            await requestState.OnDelta($"[THINKING] {content}", cancellationToken);
            return;
        }

        var isStructuredEvent = line[0] == '{';
        var prefixLineBreak = requestState.Buffer.Length > 0 && !isStructuredEvent;

        if (prefixLineBreak)
        {
            requestState.Buffer.AppendLine();
        }

        requestState.Buffer.Append(content);
        var outboundDelta = prefixLineBreak
            ? $"\n{content}"
            : content;

        await requestState.OnDelta(outboundDelta, cancellationToken);
    }

    private static CliEventParseResult TryParseCliEvent(string rawLine)
    {
        var trimmed = rawLine.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;

                var type = GetString(root, "type") ?? GetString(root, "event") ?? string.Empty;
                var content =
                    GetString(root, "delta")
                    ?? GetString(root, "content")
                    ?? GetString(root, "text")
                    ?? GetNestedString(root, "data", "delta")
                    ?? GetNestedString(root, "data", "content")
                    ?? GetNestedString(root, "data", "text")
                    ?? GetNestedString(root, "message", "content")
                    ?? GetNestedString(root, "message", "text");

                var isThinking = type.Contains("reason", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("thinking", StringComparison.OrdinalIgnoreCase);

                var toolUsed = type.Contains("tool", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("mcp", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("function", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("command", StringComparison.OrdinalIgnoreCase);

                if (!toolUsed)
                {
                    var role = GetString(root, "role") ?? GetNestedString(root, "message", "role") ?? string.Empty;
                    toolUsed = role.Contains("tool", StringComparison.OrdinalIgnoreCase);
                }

                return new CliEventParseResult(content ?? string.Empty, isThinking, toolUsed);
            }
            catch (JsonException)
            {
                return new CliEventParseResult(trimmed, false, LooksLikeToolTrace(trimmed));
            }
        }

        return new CliEventParseResult(trimmed, false, LooksLikeToolTrace(trimmed));
    }

    private static bool LooksLikeToolTrace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || value.Contains("mcp:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("run_in_terminal", StringComparison.OrdinalIgnoreCase)
            || value.Contains("apply_patch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("runTests", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string? GetNestedString(JsonElement element, string parentName, string childName)
    {
        if (!element.TryGetProperty(parentName, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(parent, childName);
    }

    private static void ApplyCliEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment[CopilotAllowAllEnvVar] = "1";
    }

    private static string BuildCliFailureMessage(int exitCode, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stderr) && IsPermissionDeniedError(stderr))
        {
            return "MCP permission denied. This session could not request interactive approval. Ensure Copilot CLI runs with --allow-all-tools (or COPILOT_ALLOW_ALL=1) and that Gmail MCP is already authorized for the runtime Windows user profile.";
        }

        return string.IsNullOrWhiteSpace(stderr)
            ? $"Copilot CLI exited with code {exitCode}."
            : $"Copilot CLI exited with code {exitCode}: {stderr}";
    }

    private static bool IsPermissionDeniedError(string stderr)
    {
        var normalized = stderr.ToLowerInvariant();
        return normalized.Contains("permission denied", StringComparison.Ordinal)
            || normalized.Contains("could not request permission from user", StringComparison.Ordinal)
            || normalized.Contains("not authorized", StringComparison.Ordinal);
    }

    private static async Task<string> ReadAllStderrAsync(Process process, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line.Trim());
        }

        return builder.ToString();
    }

    private static void SafeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore best-effort shutdown errors.
        }
    }

    private readonly record struct CliEventParseResult(string Content, bool IsThinking, bool ToolUsed);
}
