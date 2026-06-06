namespace Framedash
{
    /// <summary>
    /// Manages session and player identity for telemetry events.
    /// A new session ID is generated each time the game starts.
    /// Player ID is developer-supplied; defaults to empty (anonymous).
    /// </summary>
    public sealed class SessionManager
    {
        public string SessionId { get; }
        public string PlayerId { get; private set; }

        public SessionManager(string playerId = null)
        {
            SessionId = SessionIdGenerator.NewSessionIdV7();
            PlayerId = string.IsNullOrWhiteSpace(playerId) ? "" : playerId.Trim();
        }

        /// <summary>
        /// Update the player ID at runtime (e.g. after login).
        /// </summary>
        public void SetPlayerId(string playerId)
        {
            PlayerId = string.IsNullOrWhiteSpace(playerId) ? "" : playerId.Trim();
        }
    }
}
