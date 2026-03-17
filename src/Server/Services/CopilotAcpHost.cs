using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public interface ICopilotAcpHost
{
    long Generation { get; }

    Task<string> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<CopilotAcpPromptResult> PromptSessionAsync(
        string sessionId,
        string prompt,
        Func<CopilotAcpUpdate, CancellationToken, Task> onUpdate,
        CancellationToken cancellationToken);
}

public sealed record CopilotAcpPromptResult(string StopReason);

public sealed record CopilotAcpUpdate(string Content, bool IsThinking, bool ToolUsed);

public sealed class CopilotAcpHost : ICopilotAcpHost, IHostedService, IAsyncDisposable
{
    private const int ProtocolVersion = 1;

    private readonly CopilotOptions _options;
    private readonly ILogger<CopilotAcpHost> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PromptExecution> _activePrompts = new(StringComparer.Ordinal);

    private Process? _process;
    private Task? _stdoutReaderTask;
    private Task? _stderrReaderTask;
    private long _nextRequestId;
    private long _generation;
    private bool _disposed;
    private bool _isStopping;

    public CopilotAcpHost(IOptions<CopilotOptions> options, ILogger<CopilotAcpHost> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public long Generation => Volatile.Read(ref _generation);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        await DisposeAsync();
    }

    public async Task<string> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        var response = await SendRequestAsync(
            "session/new",
            new
            {
                cwd = workingDirectory,
                mcpServers = Array.Empty<object>()
            },
            cancellationToken);

        if (!response.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Copilot ACP session/new did not return a sessionId.");
        }

        var sessionId = sessionIdElement.GetString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("Copilot ACP session/new returned an empty sessionId.");
        }

        _logger.LogInformation(
            "Copilot ACP session created. SessionId={SessionId}, Generation={Generation}",
            sessionId,
            Generation);

        return sessionId;
    }

    public async Task<CopilotAcpPromptResult> PromptSessionAsync(
        string sessionId,
        string prompt,
        Func<CopilotAcpUpdate, CancellationToken, Task> onUpdate,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        var execution = new PromptExecution(sessionId, onUpdate, cancellationToken);
        if (!_activePrompts.TryAdd(sessionId, execution))
        {
            throw new InvalidOperationException($"A Copilot ACP prompt is already active for session '{sessionId}'.");
        }

        try
        {
            var responseTask = SendRequestAsync(
                "session/prompt",
                new
                {
                    sessionId,
                    prompt = new[]
                    {
                        new
                        {
                            type = "text",
                            text = prompt
                        }
                    }
                },
                CancellationToken.None);

            if (!cancellationToken.CanBeCanceled)
            {
                var response = await responseTask;
                return ParsePromptResponse(response);
            }

            try
            {
                var completed = await Task.WhenAny(responseTask, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
                if (completed == responseTask)
                {
                    var response = await responseTask;
                    return ParsePromptResponse(response);
                }

                await SendNotificationAsync(
                    "session/cancel",
                    new
                    {
                        sessionId
                    },
                    CancellationToken.None);

                var postCancelCompleted = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
                if (postCancelCompleted == responseTask)
                {
                    var cancelledResponse = await responseTask;
                    var promptResult = ParsePromptResponse(cancelledResponse);
                    if (string.Equals(promptResult.StopReason, "cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    return promptResult;
                }

                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            _activePrompts.TryRemove(sessionId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifecycleLock.WaitAsync();
        try
        {
            FaultAllPending(new ObjectDisposedException(nameof(CopilotAcpHost)));

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                _process.Dispose();
                _process = null;
            }
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
            _writeLock.Dispose();
        }

        if (_stdoutReaderTask != null)
        {
            try
            {
                await _stdoutReaderTask;
            }
            catch
            {
            }
        }

        if (_stderrReaderTask != null)
        {
            try
            {
                await _stderrReaderTask;
            }
            catch
            {
            }
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (IsProcessRunning())
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (IsProcessRunning())
            {
                return;
            }

            StartProcess();
            await InitializeAsync(cancellationToken);

            var generation = Interlocked.Increment(ref _generation);
            _logger.LogInformation("Copilot ACP host started. Generation={Generation}", generation);
        }
        catch
        {
            CleanupProcess();
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StartProcess()
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

        startInfo.ArgumentList.Add("--acp");
        startInfo.ArgumentList.Add("--stdio");

        if (!string.IsNullOrWhiteSpace(_options.Cli.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_options.Cli.Model);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Exited += HandleProcessExited;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start Copilot ACP process.");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to launch Copilot CLI command '{_options.Cli.Command}'. Ensure Copilot CLI is installed and available on PATH.", ex);
        }

        _process = process;
        _stdoutReaderTask = Task.Run(() => ReadStdoutLoopAsync(process));
        _stderrReaderTask = Task.Run(() => ReadStderrLoopAsync(process));

        _logger.LogInformation("Copilot ACP process started. Pid={Pid}", process.Id);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var startupTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Cli.AcpStartupTimeoutSeconds > 0)
        {
            startupTimeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.Cli.AcpStartupTimeoutSeconds));
        }

        JsonElement response;
        try
        {
            response = await SendRequestAsync(
                "initialize",
                new
                {
                    protocolVersion = ProtocolVersion,
                    clientInfo = new
                    {
                        name = "buddy-server",
                        version = "1.0.0"
                    },
                    clientCapabilities = new
                    {
                        fs = new
                        {
                            readTextFile = false,
                            writeTextFile = false
                        },
                        terminal = false
                    }
                },
                startupTimeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Copilot ACP initialization timed out after {_options.Cli.AcpStartupTimeoutSeconds} seconds.");
        }

        if (!response.TryGetProperty("protocolVersion", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException("Copilot ACP initialize response did not include a protocolVersion.");
        }

        var protocolVersion = versionElement.GetInt32();
        if (protocolVersion != ProtocolVersion)
        {
            _logger.LogWarning(
                "Copilot ACP negotiated protocol version {ProtocolVersion}. Client expects {ExpectedVersion}.",
                protocolVersion,
                ProtocolVersion);
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var requestId = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var pending = new PendingRequest(method);
        if (!_pendingRequests.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException($"Failed to register Copilot ACP request '{requestId}'.");
        }

        try
        {
            await SendMessageAsync(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters
            }, cancellationToken);

            return await pending.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    private Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        return SendMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        }, cancellationToken);
    }

    private async Task SendMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process == null || process.HasExited)
        {
            throw new InvalidOperationException("Copilot ACP process is not running.");
        }

        var json = JsonSerializer.Serialize(payload);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(Process process)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await HandleIncomingMessageAsync(line);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _logger.LogWarning(ex, "Copilot ACP stdout reader stopped unexpectedly.");
                FaultAllPending(ex);
            }
        }
    }

    private async Task ReadStderrLoopAsync(Process process)
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

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                _logger.LogDebug("Copilot ACP stderr: {Message}", line.Trim());
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _logger.LogDebug(ex, "Copilot ACP stderr reader stopped unexpectedly.");
            }
        }
    }

    private async Task HandleIncomingMessageAsync(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (root.TryGetProperty("id", out var idElement))
        {
            await HandleResponseAsync(root, idElement);
            return;
        }

        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var method = methodElement.GetString();
        if (string.IsNullOrWhiteSpace(method))
        {
            return;
        }

        if (!root.TryGetProperty("params", out var paramsElement) || paramsElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (method)
        {
            case "session/update":
                await HandleSessionUpdateAsync(paramsElement);
                break;
            case "session/request_permission":
                await HandlePermissionRequestAsync(root, paramsElement);
                break;
            default:
                _logger.LogDebug("Ignoring unsupported Copilot ACP notification/request: {Method}", method);
                break;
        }
    }

    private Task HandleResponseAsync(JsonElement root, JsonElement idElement)
    {
        var requestId = idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Number => idElement.GetRawText(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Task.CompletedTask;
        }

        if (!_pendingRequests.TryRemove(requestId, out var pending))
        {
            return Task.CompletedTask;
        }

        if (root.TryGetProperty("error", out var errorElement))
        {
            pending.TrySetException(BuildJsonRpcException(pending.Method, errorElement));
            return Task.CompletedTask;
        }

        if (!root.TryGetProperty("result", out var resultElement))
        {
            pending.TrySetException(new InvalidOperationException($"Copilot ACP method '{pending.Method}' returned neither result nor error."));
            return Task.CompletedTask;
        }

        pending.TrySetResult(resultElement.Clone());
        return Task.CompletedTask;
    }

    private async Task HandleSessionUpdateAsync(JsonElement paramsElement)
    {
        if (!paramsElement.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var sessionId = sessionIdElement.GetString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (!_activePrompts.TryGetValue(sessionId, out var execution))
        {
            return;
        }

        if (!paramsElement.TryGetProperty("update", out var updateElement) || updateElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!updateElement.TryGetProperty("sessionUpdate", out var updateTypeElement) || updateTypeElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var updateType = updateTypeElement.GetString();
        if (string.IsNullOrWhiteSpace(updateType))
        {
            return;
        }

        if (string.Equals(updateType, "tool_call", StringComparison.OrdinalIgnoreCase)
            || string.Equals(updateType, "tool_call_update", StringComparison.OrdinalIgnoreCase))
        {
            execution.ToolUsed = true;
            return;
        }

        if (!string.Equals(updateType, "agent_message_chunk", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(updateType, "agent_thought_chunk", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!updateElement.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!contentElement.TryGetProperty("type", out var contentTypeElement)
            || contentTypeElement.ValueKind != JsonValueKind.String
            || !string.Equals(contentTypeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!contentElement.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = textElement.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var update = new CopilotAcpUpdate(
            text,
            string.Equals(updateType, "agent_thought_chunk", StringComparison.OrdinalIgnoreCase),
            execution.ToolUsed);

        await execution.OnUpdate(update, execution.CancellationToken);
    }

    private async Task HandlePermissionRequestAsync(JsonElement root, JsonElement paramsElement)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            return;
        }

        if (!paramsElement.TryGetProperty("options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string? selectedOptionId = null;
        foreach (var option in optionsElement.EnumerateArray())
        {
            if (!option.TryGetProperty("optionId", out var optionIdElement) || optionIdElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!option.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var kind = kindElement.GetString();
            if (string.Equals(kind, "allow_always", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "allow_once", StringComparison.OrdinalIgnoreCase))
            {
                selectedOptionId = optionIdElement.GetString();
                if (string.Equals(kind, "allow_always", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        object outcomePayload;
        if (selectedOptionId != null)
        {
            outcomePayload = new
            {
                outcome = "selected",
                optionId = selectedOptionId
            };
        }
        else
        {
            outcomePayload = new
            {
                outcome = "cancelled"
            };
        }

        var responseId = idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Number => idElement.GetRawText(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(responseId))
        {
            return;
        }

        await SendMessageAsync(new
        {
            jsonrpc = "2.0",
            id = responseId,
            result = new
            {
                outcome = outcomePayload
            }
        }, CancellationToken.None);
    }

    private static CopilotAcpPromptResult ParsePromptResponse(JsonElement response)
    {
        if (!response.TryGetProperty("stopReason", out var stopReasonElement) || stopReasonElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Copilot ACP prompt response did not include a stopReason.");
        }

        var stopReason = stopReasonElement.GetString();
        if (string.IsNullOrWhiteSpace(stopReason))
        {
            throw new InvalidOperationException("Copilot ACP prompt response included an empty stopReason.");
        }

        return new CopilotAcpPromptResult(stopReason);
    }

    private Exception BuildJsonRpcException(string method, JsonElement errorElement)
    {
        var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : 0;

        var message = errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : "Unknown ACP error.";

        return new InvalidOperationException($"Copilot ACP method '{method}' failed with code {code}: {message}");
    }

    private bool IsProcessRunning()
    {
        return _process is { HasExited: false };
    }

    private void HandleProcessExited(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var process = sender as Process;
        var exitCode = process?.HasExited == true ? process.ExitCode : -1;
        var exception = new InvalidOperationException($"Copilot ACP process exited unexpectedly with code {exitCode}.");

        if (!_isStopping)
        {
            _logger.LogWarning("Copilot ACP process exited. ExitCode={ExitCode}", exitCode);
        }

        FaultAllPending(exception);
        CleanupProcess();
    }

    private void CleanupProcess()
    {
        if (_process != null)
        {
            try
            {
                _process.Exited -= HandleProcessExited;
            }
            catch
            {
            }

            _process.Dispose();
            _process = null;
        }
    }

    private void FaultAllPending(Exception exception)
    {
        foreach (var entry in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(entry.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<JsonElement> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(string method)
        {
            Method = method;
        }

        public string Method { get; }

        public Task<JsonElement> Task => _tcs.Task;

        public void TrySetResult(JsonElement result)
        {
            _tcs.TrySetResult(result);
        }

        public void TrySetException(Exception exception)
        {
            _tcs.TrySetException(exception);
        }
    }

    private sealed class PromptExecution
    {
        public PromptExecution(string sessionId, Func<CopilotAcpUpdate, CancellationToken, Task> onUpdate, CancellationToken cancellationToken)
        {
            SessionId = sessionId;
            OnUpdate = onUpdate;
            CancellationToken = cancellationToken;
        }

        public string SessionId { get; }

        public Func<CopilotAcpUpdate, CancellationToken, Task> OnUpdate { get; }

        public CancellationToken CancellationToken { get; }

        public bool ToolUsed { get; set; }
    }
}