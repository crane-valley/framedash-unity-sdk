using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class EventBufferTests
    {
        [Test]
        public void Enqueue_Single_DequeueAll_ReturnsSingleEvent()
        {
            var buffer = new EventBuffer(10);
            var evt = new TelemetryEvent { EventName = "test" };

            buffer.Enqueue(evt);
            var result = buffer.DequeueAll();

            Assert.That(result, Has.Length.EqualTo(1));
            Assert.That(result[0].EventName, Is.EqualTo("test"));
        }

        [Test]
        public void DequeueAll_PreservesFIFOOrder()
        {
            var buffer = new EventBuffer(10);
            buffer.Enqueue(new TelemetryEvent { EventName = "a" });
            buffer.Enqueue(new TelemetryEvent { EventName = "b" });
            buffer.Enqueue(new TelemetryEvent { EventName = "c" });

            var result = buffer.DequeueAll();

            Assert.That(result, Has.Length.EqualTo(3));
            Assert.That(result[0].EventName, Is.EqualTo("a"));
            Assert.That(result[1].EventName, Is.EqualTo("b"));
            Assert.That(result[2].EventName, Is.EqualTo("c"));
        }

        [Test]
        public void DequeueAll_EmptyBuffer_ReturnsEmptyArray()
        {
            var buffer = new EventBuffer(10);
            var result = buffer.DequeueAll();

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void RingBuffer_DropsOldestWhenFull()
        {
            var buffer = new EventBuffer(3);
            buffer.Enqueue(new TelemetryEvent { EventName = "a" });
            buffer.Enqueue(new TelemetryEvent { EventName = "b" });
            buffer.Enqueue(new TelemetryEvent { EventName = "c" });
            // Buffer full -- next enqueue drops oldest ("a")
            buffer.Enqueue(new TelemetryEvent { EventName = "d" });

            var result = buffer.DequeueAll();

            Assert.That(result, Has.Length.EqualTo(3));
            Assert.That(result[0].EventName, Is.EqualTo("b"));
            Assert.That(result[1].EventName, Is.EqualTo("c"));
            Assert.That(result[2].EventName, Is.EqualTo("d"));
        }

        [Test]
        public void TryEnqueuePreservingOldest_WhenFull_RejectsIncomingEvent()
        {
            var buffer = new EventBuffer(3);
            buffer.Enqueue(new TelemetryEvent { EventName = "persisted-a" });
            buffer.Enqueue(new TelemetryEvent { EventName = "persisted-b" });
            buffer.Enqueue(new TelemetryEvent { EventName = "fresh-c" });

            bool accepted = buffer.TryEnqueuePreservingOldest(
                new TelemetryEvent { EventName = "fresh-d" });

            Assert.That(accepted, Is.False);
            Assert.That(buffer.DequeueAll().Select(evt => evt.EventName), Is.EqualTo(new[]
            {
                "persisted-a",
                "persisted-b",
                "fresh-c",
            }));
        }

        [Test]
        public void Count_ReflectsEnqueuedItems()
        {
            var buffer = new EventBuffer(10);
            Assert.That(buffer.Count, Is.EqualTo(0));

            buffer.Enqueue(new TelemetryEvent { EventName = "a" });
            Assert.That(buffer.Count, Is.EqualTo(1));

            buffer.Enqueue(new TelemetryEvent { EventName = "b" });
            Assert.That(buffer.Count, Is.EqualTo(2));
        }

        [Test]
        public void Count_DoesNotExceedCapacity()
        {
            var buffer = new EventBuffer(2);
            buffer.Enqueue(new TelemetryEvent { EventName = "a" });
            buffer.Enqueue(new TelemetryEvent { EventName = "b" });
            buffer.Enqueue(new TelemetryEvent { EventName = "c" });

            Assert.That(buffer.Count, Is.EqualTo(2));
            Assert.That(buffer.Capacity, Is.EqualTo(2));
        }

        [Test]
        public void DequeueAll_ResetsBuffer()
        {
            var buffer = new EventBuffer(10);
            buffer.Enqueue(new TelemetryEvent { EventName = "a" });
            buffer.DequeueAll();

            var second = buffer.DequeueAll();
            Assert.That(second, Is.Empty);
            Assert.That(buffer.Count, Is.EqualTo(0));
        }

        [Test]
        public void ConcurrentEnqueue_IsThreadSafe()
        {
            var buffer = new EventBuffer(1000);
            const int threadsCount = 4;
            const int eventsPerThread = 100;
            var barrier = new Barrier(threadsCount);
            var threads = new Thread[threadsCount];

            for (int t = 0; t < threadsCount; t++)
            {
                int threadId = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < eventsPerThread; i++)
                    {
                        buffer.Enqueue(new TelemetryEvent
                        {
                            EventName = $"t{threadId}_e{i}"
                        });
                    }
                });
                threads[t].Start();
            }

            foreach (var thread in threads)
                thread.Join();

            var result = buffer.DequeueAll();
            Assert.That(result, Has.Length.EqualTo(threadsCount * eventsPerThread));

            // Verify every unique event name is present (no slot overwrites)
            var names = new HashSet<string>(result.Select(e => e.EventName));
            Assert.That(names.Count, Is.EqualTo(threadsCount * eventsPerThread));
        }

        [Test]
        public void DequeueAll_ThenReEnqueue_WorksCorrectly()
        {
            var buffer = new EventBuffer(5);
            buffer.Enqueue(new TelemetryEvent { EventName = "first" });
            buffer.Enqueue(new TelemetryEvent { EventName = "second" });
            buffer.DequeueAll();

            // Re-enqueue after reset
            buffer.Enqueue(new TelemetryEvent { EventName = "third" });
            buffer.Enqueue(new TelemetryEvent { EventName = "fourth" });

            var result = buffer.DequeueAll();
            Assert.That(result, Has.Length.EqualTo(2));
            Assert.That(result[0].EventName, Is.EqualTo("third"));
            Assert.That(result[1].EventName, Is.EqualTo("fourth"));
        }

        [Test]
        public void DefaultCapacity_MatchesRuntimeSpec()
        {
            Assert.That(EventBuffer.DefaultCapacity, Is.EqualTo(10000));
        }

        [Test]
        public void InvalidCapacity_FallsBackToDefaultCapacity()
        {
            var buffer = new EventBuffer(0);
            Assert.That(buffer.Capacity, Is.EqualTo(EventBuffer.DefaultCapacity));

            var bufferNeg = new EventBuffer(-5);
            Assert.That(bufferNeg.Capacity, Is.EqualTo(EventBuffer.DefaultCapacity));
        }
    }
}
