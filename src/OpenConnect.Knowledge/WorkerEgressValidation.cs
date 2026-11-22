using System.Text;
using OpenConnect.Privacy;
using OpenConnect.Abstractions;

namespace OpenConnect.Knowledge;

internal static class WorkerEgressValidation
{
    internal static bool IsSafe(string source, EgressDecision? decision)
    {
        if (decision is null || !decision.Allowed || !Enum.IsDefined(decision.Action) ||
            decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review ||
            decision.Reason == "privacy_enforcement_disabled" || decision.SanitizedContent is null || decision.DetectedEntities is null)
            return false;
        var entities = decision.DetectedEntities;
        if (entities.Count == 0) return true;
        if (entities.Count > 20_000 || (long)entities.Count * decision.SanitizedContent.Length > 100_000_000 ||
            string.Equals(source, decision.SanitizedContent, StringComparison.Ordinal)) return false;
        var offsets = new List<int>();
        var offset = 0;
        foreach (var rune in source.EnumerateRunes()) { offsets.Add(offset); offset += rune.Utf16SequenceLength; }
        offsets.Add(offset);
        foreach (var entity in entities)
        {
            if (entity is null || entity.Start < 0 || entity.Length <= 0 ||
                (long)entity.Start + entity.Length >= offsets.Count) return false;
            var start = offsets[entity.Start];
            var length = offsets[entity.Start + entity.Length] - start;
            if (decision.SanitizedContent.AsSpan().Contains(source.AsSpan(start, length), StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
