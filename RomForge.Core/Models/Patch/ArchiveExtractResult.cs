namespace RomForge.Core.Models.Patch;

public sealed class ArchiveExtractResult
{
    public string? ResolvedPath { get; init; }

    public IReadOnlyList<ArchiveCandidate> Candidates { get; init; } = [];

    public bool NeedsSelection => ResolvedPath is null;
}