using Amc.Core.Domain;

namespace Amc.Core.Events;

/// <summary>Hunk-level overlap of the conflict with this key (<see cref="EventTypes.ConflictAnalyzed"/>).</summary>
public sealed record ConflictAnalysisData(string ConflictKey, ConflictOverlap Overlap);

/// <summary>A warning sent to the agents of a conflict (<see cref="EventTypes.ConflictWarned"/>).</summary>
public sealed record ConflictWarnedData(string ConflictKey, bool Auto, List<ConflictWarningRecipient> Recipients);
