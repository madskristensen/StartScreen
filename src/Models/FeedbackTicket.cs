using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StartScreen.Models
{
    public sealed class FeedbackTicket
    {
        public long Id { get; internal set; }
        public string Title { get; internal set; }
        public string Url { get; internal set; }
        public string State { get; internal set; }
        public int Votes { get; internal set; }
        public bool IsCreatedByUser { get; internal set; }
        public bool IsUpvotedByUser { get; internal set; }
        public DateTime UpdatedUtc { get; internal set; }

        public string Relationship => IsCreatedByUser
            ? (IsUpvotedByUser ? "Created and upvoted by you" : "Created by you")
            : "Upvoted by you";

        private string NormalizedState => new string((State ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();

        public string StateText => NormalizedState switch
        {
            "new" => "New",
            "triaged" => "Triaged",
            "pending" or "underreview" => "Under Review",
            "consideration" or "underconsideration" => "Under Consideration",
            "investigation" or "underinvestigation" => "Under Investigation",
            "roadmap" or "onroadmap" => "On Roadmap",
            "notenoughinfo" => "Not enough info",
            "needmoreinfo" or "needsmoreinfo" or "needmoreinformation" or "needsmoreinformation" => "Need More Info",
            "duplicate" or "closedduplicate" => "Closed - Duplicate",
            "fixed" or "closedfixed" => "Closed - Fixed",
            _ => State,
        };

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
                    "new" or "triaged" or "pending" or "consideration" or "underconsideration" or "underreview" => "2E6FC2",
                    "notenoughinfo" or "needmoreinfo" or "needsmoreinfo" or "needmoreinformation" or "needsmoreinformation" => "DC143C",
                    "investigation" or "roadmap" or "underinvestigation" or "onroadmap" => "925900",
                    _ => "606060",
                };
            }
        }

        public string Details => $"{Relationship} - {Votes} votes";
        public string UpdatedText => $"Updated {UpdatedUtc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture)}";
        public string ToolTip => $"#{Id} - {UpdatedUtc.ToLocalTime():F}";
    }

    internal sealed class FeedbackSnapshot
    {
        internal long PrincipalId { get; }
        internal IReadOnlyList<FeedbackTicket> Tickets { get; }
        internal DateTime UpdatedUtc { get; }

        internal FeedbackSnapshot(long principalId, IReadOnlyList<FeedbackTicket> tickets, DateTime updatedUtc)
        {
            PrincipalId = principalId;
            Tickets = tickets;
            UpdatedUtc = updatedUtc;
        }
    }
}