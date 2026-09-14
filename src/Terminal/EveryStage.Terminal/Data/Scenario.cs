namespace EveryStage.Terminal.Data;

/// <summary>
/// A saveable/switchable set of activities, e.g. "周一晨会方案" (PLANNING.md §6). Exactly one
/// scenario is "current" at a time in <see cref="ScenarioRepository"/>.
/// </summary>
public sealed class Scenario
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public List<Activity> Activities { get; init; } = new();
}
