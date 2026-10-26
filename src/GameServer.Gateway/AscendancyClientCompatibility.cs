/// <summary>
/// Phrecia's shipped graph has older ordinary topology. Installing a merged graph is outside normal startup:
/// an agreed client-compatible graph is a separate prerequisite. Reject only Phrecia requests, before any save.
/// </summary>
static class AscendancyClientCompatibility
{
    public static void RequirePlayable(AscendancyIdentity? selection)
    {
        if (selection?.Family == AscendancyFamily.Phrecia)
            throw new AscendancyRuleException("Phrecia play is gated pending an agreed client-compatible passive graph; " +
                "the installed client archive is not changed. Ordinary ascendancies remain available. " +
                "See Agents/docs/game/ascendancies.md");
    }
}
