using UnityEngine;

namespace Framedash
{
    /// <summary>
    /// Thread-safe ring buffer for telemetry events.
    /// When full, oldest events are dropped (game perf > telemetry completeness).
    /// </summary>
    public sealed class EventBuffer
    {
        private readonly TelemetryEvent[] _buffer;
        private readonly object _lock = new object();
        private int _head;
        private int _tail;
        private int _count;
        private int _droppedCount;
        private int _rejectedNewestCount;
        public static readonly int DefaultCapacity = 10000;
        private const int DropLogInterval = 100;

        public int Count
        {
            get { lock (_lock) return _count; }
        }

        /// <summary>
        /// Cumulative number of events dropped because the ring was full (oldest evicted).
        /// Monotonic for the buffer's lifetime; the offline queue uses it to detect when a
        /// burst has evicted restored (persisted) events from the head.
        /// </summary>
        public int DroppedCount
        {
            get { lock (_lock) return _droppedCount; }
        }

        public int Capacity => _buffer.Length;

        public EventBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                Debug.LogWarning($"[Framedash] EventBuffer capacity must be > 0. Using default {DefaultCapacity}.");
                capacity = DefaultCapacity;
            }
            _buffer = new TelemetryEvent[capacity];
        }

        /// <summary>Add an event. Drops oldest if full.</summary>
        public void Enqueue(TelemetryEvent evt)
        {
            EnqueueInternal(evt, preserveOldestWhenFull: false);
        }

        /// <summary>
        /// Add an event without evicting the oldest entry when the buffer is full.
        /// </summary>
        /// <returns>False when the incoming event was rejected.</returns>
        internal bool TryEnqueuePreservingOldest(TelemetryEvent evt)
        {
            return EnqueueInternal(evt, preserveOldestWhenFull: true);
        }

        private bool EnqueueInternal(TelemetryEvent evt, bool preserveOldestWhenFull)
        {
            lock (_lock)
            {
                if (_count == _buffer.Length && preserveOldestWhenFull)
                {
                    _rejectedNewestCount++;
                    if (_rejectedNewestCount % DropLogInterval == 1)
                    {
                        Debug.LogWarning(
                            $"[Framedash] Event buffer full with a durable prefix — {_rejectedNewestCount} incoming event(s) rejected so far.");
                    }
                    return false;
                }

                _buffer[_tail] = evt;
                _tail = (_tail + 1) % _buffer.Length;

                if (_count == _buffer.Length)
                {
                    // Ring buffer full — advance head (drop oldest)
                    _head = (_head + 1) % _buffer.Length;
                    _droppedCount++;

                    if (_droppedCount % DropLogInterval == 1)
                    {
                        Debug.LogWarning($"[Framedash] Event buffer full — {_droppedCount} event(s) dropped so far.");
                    }
                }
                else
                {
                    _count++;
                }

                return true;
            }
        }

        /// <summary>Dequeue all buffered events and reset.</summary>
        public TelemetryEvent[] DequeueAll()
        {
            lock (_lock)
            {
                if (_count == 0) return System.Array.Empty<TelemetryEvent>();

                var result = new TelemetryEvent[_count];
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head + i) % _buffer.Length;
                    result[i] = _buffer[idx];
                    _buffer[idx] = default; // Release references for GC
                }

                _head = 0;
                _tail = 0;
                _count = 0;

                return result;
            }
        }
    }
}
