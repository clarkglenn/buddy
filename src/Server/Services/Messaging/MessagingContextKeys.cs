namespace Buddy.Server.Services.Messaging;

internal static class MessagingContextKeys
{
    internal const string Channel = "channel";
    internal const string ThreadTs = "thread_ts";
    internal const string ReplyTs = "reply_ts";
    internal const string UpdateTs = "update_ts";
    internal const string IsSlashCommand = "is_slash_command";
}