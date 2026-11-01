using CalendarMcp.Core.Models;
using Google.Apis.Gmail.v1.Data;

namespace CalendarMcp.Core.Changes.Google;

/// <summary>
/// What a page of Gmail history comes to for the Inbox. History lists every step a message took,
/// so one id can appear many times: what counts is whether it was in the Inbox before the page
/// and whether it is after. A mail that came and went inside one page was never there for Aura.
/// </summary>
internal static class GmailHistory
{
    public const string Inbox = "INBOX";

    public static IReadOnlyList<(string Id, string Type)> Net(IEnumerable<History> records)
    {
        var tracked = new Dictionary<string, Presence>();
        var order = new List<string>();

        foreach (var record in records)
        {
            foreach (var added in record.MessagesAdded ?? [])
            {
                if (added.Message?.LabelIds?.Contains(Inbox) == true)
                    Note(added.Message.Id, Step.Enters);
            }

            foreach (var label in record.LabelsAdded ?? [])
            {
                if (label.LabelIds?.Contains(Inbox) == true)
                    Note(label.Message.Id, Step.Enters);
                else if (label.Message?.LabelIds?.Contains(Inbox) == true)
                    Note(label.Message.Id, Step.ChangesInside);
            }

            foreach (var label in record.LabelsRemoved ?? [])
            {
                if (label.LabelIds?.Contains(Inbox) == true)
                    Note(label.Message.Id, Step.Leaves);
                else if (label.Message?.LabelIds?.Contains(Inbox) == true)
                    Note(label.Message.Id, Step.ChangesInside);
            }

            // Gmail's documentation does not say what a deleted message's labels hold. Labels that are
            // there and leave the Inbox out rule it out; otherwise a deletion of an unknown id is harmless.
            foreach (var deleted in record.MessagesDeleted ?? [])
            {
                var labels = deleted.Message?.LabelIds;
                if (deleted.Message is not null && (labels is not { Count: > 0 } || labels.Contains(Inbox)))
                    Note(deleted.Message.Id, Step.Leaves);
            }
        }

        return order
            .Select(id => (Id: id, Type: tracked[id].Type))
            .Where(change => change.Type is not null)
            .Select(change => (change.Id, change.Type!))
            .ToList();

        void Note(string id, Step step)
        {
            if (!tracked.TryGetValue(id, out var presence))
            {
                presence = new Presence(WasIn: step != Step.Enters);
                tracked[id] = presence;
                order.Add(id);
            }

            presence.IsIn = step != Step.Leaves;
        }
    }

    private enum Step
    {
        Enters,
        ChangesInside,
        Leaves
    }

    private sealed class Presence(bool WasIn)
    {
        public bool IsIn { get; set; }

        public string? Type => (WasIn, IsIn) switch
        {
            (false, true) => ChangeType.Added,
            (true, false) => ChangeType.Deleted,
            (true, true) => ChangeType.Updated,
            _ => null
        };
    }
}
