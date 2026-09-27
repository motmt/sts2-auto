using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver.Engine.InCombat.Extensions;

internal static class CombatManagerExtensions
{
    public static CombatTurnState? GetLiveTurnState(this CombatManager combatManager)
        => combatManager._turnState is { IsInProgress: true } turnState
        ? turnState
        : null;

    public static CombatState? GetLiveCombatState(this CombatManager combatManager)
        => combatManager.GetLiveTurnState()?.State;
}
