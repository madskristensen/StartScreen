using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace StartScreen.Models
{
    public enum FeedbackTicketType
    {
        Unknown = -1,
        Problem = 0,
        Suggestion = 3,
    }

    public sealed class FeedbackTicket
    {
        [JsonInclude]
        public long Id { get; internal set; }
        [JsonInclude]
        public string Title { get; internal set; }
        [JsonInclude]
        public string Url { get; internal set; }
        [JsonInclude]
        public string State { get; internal set; }
        [JsonInclude]
        public FeedbackTicketType Type { get; internal set; } = FeedbackTicketType.Unknown;
        [JsonInclude]
        public int Votes { get; internal set; }
        [JsonInclude]
        public bool IsCreatedByUser { get; internal set; }
        [JsonInclude]
        public bool IsUpvotedByUser { get; internal set; }
        [JsonInclude]
        public DateTime UpdatedUtc { get; internal set; }

        [JsonIgnore]
        public string Relationship => IsCreatedByUser
            ? (IsUpvotedByUser ? "Created and upvoted by you" : "Created by you")
            : "Upvoted by you";

        private string NormalizedState => new string((State ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();

        [JsonIgnore]
        public string StateText => NormalizedState switch
        {
            "new" => "New",
            "triaged" => "Triaged",
            "underreview" => "Under Review",
            "pending" or "pendingrelease" or "fixedpendingrelease" => Type == FeedbackTicketType.Suggestion
                ? "Completed - Preview" : "Fixed - Pending Release",
            "consideration" or "underconsideration" => Type == FeedbackTicketType.Suggestion
                ? "Under Review" : "Under Consideration",
            "investigation" or "underinvestigation" => "Under Investigation",
            "roadmap" or "onroadmap" => "On Roadmap",
            "notenoughinfo" => "Not enough info",
            "needmoreinfo" or "needsmoreinfo" or "needmoreinformation" or "needsmoreinformation" => "Need More Info",
            "duplicate" or "closedduplicate" => "Closed - Duplicate",
            "fixed" or "closedfixed" => Type == FeedbackTicketType.Suggestion ? "Completed - Release" : "Closed - Fixed",
            "completedpreview" => "Completed - Preview",
            "completedrelease" => "Completed - Release",
            _ => State,
        };

        [JsonIgnore]
        public string StateColor
        {
            get
            {
                var state = NormalizedState;
                // Developer Community status colors; high contrast uses system brushes in the view.
                return state switch
                {
                    _ when state == "fixed" || state.StartsWith("closedfixed", StringComparison.Ordinal)
                        || state.StartsWith("completed", StringComparison.Ordinal) => "2E7D1C",
                    "pending" or "pendingrelease" or "fixedpendingrelease" => "2E7D1C",
                    "new" or "triaged" or "consideration" or "underconsideration" or "underreview" => "2E6FC2",
                    "notenoughinfo" or "needmoreinfo" or "needsmoreinfo" or "needmoreinformation" or "needsmoreinformation" => "DC143C",
                    "investigation" or "roadmap" or "underinvestigation" or "onroadmap" => "925900",
                    _ => "606060",
                };
            }
        }

        [JsonIgnore]
        public string Details => $"{Relationship} - {Votes} votes";
        [JsonIgnore]
        public string UpdatedText => $"Updated {UpdatedUtc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture)}";
        [JsonIgnore]
        public string ToolTip => $"#{Id} - {UpdatedUtc.ToLocalTime():F}";
    }

    internal sealed class FeedbackSnapshot
    {
        public long PrincipalId { get; }
        public IReadOnlyList<FeedbackTicket> Tickets { get; }
        public DateTime UpdatedUtc { get; }

        public FeedbackSnapshot(long principalId, IReadOnlyList<FeedbackTicket> tickets, DateTime updatedUtc)
        {
            PrincipalId = principalId;
            Tickets = tickets;
            UpdatedUtc = updatedUtc;
        }
    }
}