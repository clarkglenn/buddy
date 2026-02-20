using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Server.Options;
using Server.Services;
using Xunit;

namespace IntegrationTests;

public sealed class CopilotToolUsePolicyTests
{
    [Fact]
    public void IsTrivialPrompt_returns_true_for_short_factual_question()
    {
        var client = CreateClient();

        var result = InvokePrivate<bool>(client, "IsTrivialPrompt", "What is UTC?");

        Assert.True(result);
    }

    [Fact]
    public void IsTrivialPrompt_returns_false_for_code_request()
    {
        var client = CreateClient();

        var result = InvokePrivate<bool>(client, "IsTrivialPrompt", "Implement a C# function to parse JSON and add tests.");

        Assert.False(result);
    }

    [Fact]
    public void EnforceToolUsePolicy_throws_for_non_trivial_without_tool_use()
    {
        var client = CreateClient();

        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokePrivate<object?>(
                client,
                "EnforceToolUsePolicy",
                "Fix this bug in my repository and run tests",
                false,
                false,
                "Here is a direct answer without tools"));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(
            "Policy requires using MCP/CLI tools for non-trivial requests. Ask a concise factual question for direct Q&A, or rephrase the request to run through tools.",
            exception.InnerException?.Message);
    }

    [Fact]
    public void EnforceToolUsePolicy_allows_trivial_without_tool_use()
    {
        var client = CreateClient();

        var exception = Record.Exception(() =>
            InvokePrivate<object?>(
                client,
                "EnforceToolUsePolicy",
                "What is UTC?",
                true,
                false,
                "UTC is Coordinated Universal Time."));

        Assert.Null(exception);
    }

    private static CopilotClient CreateClient()
    {
        var options = Options.Create(new CopilotOptions
        {
            Model = "gpt-5.2-test",
            ToolUsePolicy = new ToolUsePolicyOptions
            {
                Enabled = true,
                AllowDirectResponsesForTrivialQuestions = true,
                TrivialQuestionMaxChars = 220,
                FailImmediatelyOnViolation = true,
                ViolationMessage = "Policy requires using MCP/CLI tools for non-trivial requests. Ask a concise factual question for direct Q&A, or rephrase the request to run through tools."
            }
        });

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        return new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            new TestMcpServerResolver());
    }

    private static T InvokePrivate<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(instance, args);
        return (T)result!;
    }

    private sealed class TestMcpServerResolver : IMcpServerResolver
    {
        public McpServerResolution Resolve()
        {
            return new McpServerResolution(new Dictionary<string, object>(), null, []);
        }
    }
}
