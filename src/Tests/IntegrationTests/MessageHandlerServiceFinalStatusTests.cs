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
    public void BuildDefinitiveFinalMessage_keeps_informational_summary_without_failure_suffix()
    {
        const string message = "Here are summaries of your last 3 emails from today.";

        var result = InvokePrivateStatic<string>("BuildDefinitiveFinalMessage", message);

        Assert.Equal(message, result);
        Assert.DoesNotContain("couldn’t confirm", result, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void SanitizeForUserFacingOutput_removes_tool_trace_lines()
    {
        const string content = "● gmail-search_emails: in:inbox└ ID: 19c9b225d1e95c20\nDe fem senaste mailen innehåller:\n• Mail 1\n• Mail 2";

        var result = InvokePrivateStatic<string>("SanitizeForUserFacingOutput", content);

        Assert.Equal("De fem senaste mailen innehåller:\n• Mail 1\n• Mail 2", result);
    }

    [Fact]
    public void SanitizeForUserFacingOutput_can_preserve_leading_newline_for_streaming()
    {
        const string content = "\n- Punkt 1";

        var result = InvokePrivateStatic<string>("SanitizeForUserFacingOutput", content, false);

        Assert.Equal("\n- Punkt 1", result);
    }

    [Fact]
    public void SanitizeForUserFacingOutput_formats_inline_dash_list_to_multiline_bullets()
    {
        const string content = "Här är en sammanfattning: Facebook - SkiStar - Base44 - Substack";

        var result = InvokePrivateStatic<string>("SanitizeForUserFacingOutput", content);

        Assert.Equal("Här är en sammanfattning:\n- Facebook\n- SkiStar\n- Base44\n- Substack", result);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var methods = typeof(MessageHandlerService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        var method = methods.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            if (parameters.Length != args.Length)
            {
                return false;
            }

            for (var i = 0; i < parameters.Length; i++)
            {
                var arg = args[i];
                if (arg == null)
                {
                    continue;
                }

                if (!parameters[i].ParameterType.IsInstanceOfType(arg))
                {
                    return false;
                }
            }

            return true;
        });

        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        return (T)result!;
    }
}
