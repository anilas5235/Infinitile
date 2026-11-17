namespace Engine.Scripts.Diagnostics{
    /// <summary>
    ///     True while a measurement run or a test drives the engine. The player's saved choices (draw distance,
    ///     frame cap) are then not applied, so the run builds the world it asks for and keeps the frame cap it set.
    ///     Set before the game scene loads and kept for the rest of the play session.
    /// </summary>
    public static class AutomatedRun{
        /// <summary>A measurement run or a test drives the engine; the player's saved choices stay unused.</summary>
        public static bool Active{ get; set; }
    }
}
