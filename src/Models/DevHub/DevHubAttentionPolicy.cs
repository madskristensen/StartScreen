using System;

namespace StartScreen.Models.DevHub
{
    internal static class DevHubAttentionPolicy
    {
        internal static bool HasRecentActivity(DateTime updatedAt, DateTime createdAt, DateTime utcNow)
        {
            var activity = updatedAt == default ? createdAt : updatedAt;
            return activity != default && activity.ToUniversalTime() >= utcNow.AddDays(-30);
        }
    }
}