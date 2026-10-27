namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Test tiers. A plain run leaves out every explicit test, so it holds only the fast tests; the Editor's runner
    ///     cannot exclude a category, so the slow tests are explicit as well as categorised.
    ///     <list type="bullet">
    ///         <item><see cref="Slow" />: tests of 5 s or more per method (world-scale comparisons, statistics);
    ///         run with explicit tests included before a pull request.</item>
    ///         <item><see cref="Report" />: tests that print values and assert nothing, run on request.</item>
    ///     </list>
    /// </summary>
    internal static class TestTiers{
        public const string Slow = "Slow";
        public const string SlowReason = "Slow: runs with the explicit tests before a pull request";
        public const string Report = "Report";
        public const string ReportReason = "Report: prints values only, run on request";
    }
}
