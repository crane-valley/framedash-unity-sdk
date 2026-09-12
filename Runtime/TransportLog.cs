namespace Framedash
{
    /// <summary>
    /// Pure formatting helpers for the opt-in verbose transport log lines (F29),
    /// extracted so the message shape is unit-testable without UnityEngine. Kept in
    /// step with the Godot SDK's TransportLog so the three SDKs emit the same
    /// delivery-confirmation vocabulary.
    /// </summary>
    public static class TransportLog
    {
        public static string FormatFlushSuccess(int eventCount, long statusCode)
            => $"[Framedash] Flushed {eventCount} {(eventCount == 1 ? "event" : "events")} (HTTP {statusCode}).";

        /// <summary>
        /// Carries the endpoint and the compressed payload size so an integrator can confirm
        /// WHERE telemetry is going and HOW BIG each batch is, mirroring the UE5 SDK's
        /// "SendBatch: N events -> endpoint" + "Payload: N bytes" pair.
        /// </summary>
        public static string FormatSendAttempt(int eventCount, int payloadBytes, string endpointUrl)
            => $"[Framedash] Sending {eventCount} {(eventCount == 1 ? "event" : "events")} " +
               $"({payloadBytes} bytes gzip) -> {endpointUrl}";
    }
}
