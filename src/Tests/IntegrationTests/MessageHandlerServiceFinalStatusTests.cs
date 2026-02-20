using System.Reflection;
using Buddy.Server.Services.Messaging;
using Xunit;

namespace IntegrationTests;

public sealed class MessageHandlerServiceFinalStatusTests
{
    [Theory]
    [InlineData("Send an email to me")]
    [InlineData("Please email to glenn@example.com")]
    [InlineData("Use gmail and send Hej")]
    public void LooksLikeEmailRequest_detects_email_intent(string prompt)
    {
        var result = InvokePrivateStatic<bool>("LooksLikeEmailRequest", prompt);

        Assert.True(result);
    }

    [Fact]
    public void HasEmailMcpCapability_returns_true_for_email_tools()
    {
        var toolNames = new[] { "mcp:gmail.send_email", "mcp:filesystem.read" };

        var result = InvokePrivateStatic<bool>("HasEmailMcpCapability", (object)toolNames);

        Assert.True(result);
    }

    [Fact]
    public void HasEmailMcpCapability_returns_false_without_email_tools()
    {
        var toolNames = new[] { "mcp:filesystem.read", "mcp:playwright.navigate" };

        var result = InvokePrivateStatic<bool>("HasEmailMcpCapability", (object)toolNames);

        Assert.False(result);
    }

    [Fact]
    public void BuildDefinitiveFinalMessage_keeps_definitive_success_text()
    {
        const string message = "Sent an email to glenn@example.com with body Hej (message id: 123).";

        var result = InvokePrivateStatic<string>("BuildDefinitiveFinalMessage", message);

        Assert.Equal(message, result);
    }

    [Fact]
    public void BuildDefinitiveFinalMessage_marks_ambiguous_text_as_failure()
    {
        const string message = "I will work on this now.";

        var result = InvokePrivateStatic<string>("BuildDefinitiveFinalMessage", message);

        Assert.Contains("couldn’t confirm", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("❌", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeForUserFacingOutput_removes_internal_progress_and_pwsh_diagnostics()
    {
        const string content = "I’ll inspect the repo next.\nThe feature is now enabled.\nSince CLI commands aren’t runnable here because pwsh isn’t available.";

        var result = InvokePrivateStatic<string>("SanitizeForUserFacingOutput", content);

        Assert.Equal("The feature is now enabled.", result);
    }

    [Fact]
    public void BuildDefinitiveFinalMessage_uses_sanitized_user_facing_content()
    {
        const string content = "I’m going to check tools first.\n✅ Completed successfully.";

        var result = InvokePrivateStatic<string>("BuildDefinitiveFinalMessage", content);

        Assert.Equal("✅ Completed successfully.", result);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(MessageHandlerService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        return (T)result!;
    }
}
