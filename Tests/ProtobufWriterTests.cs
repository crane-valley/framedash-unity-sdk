using System;
using System.Text;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class ProtobufWriterTests
    {
        [Test]
        public void WriteVarint_SingleByte_Values()
        {
            var w = new ProtobufWriter();

            w.WriteVarint(0);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x00 }));

            w.Reset();
            w.WriteVarint(1);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x01 }));

            w.Reset();
            w.WriteVarint(127);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x7F }));
        }

        [Test]
        public void WriteVarint_MultiByte_Values()
        {
            var w = new ProtobufWriter();

            // 128 = 0x80 -> varint: [0x80, 0x01]
            w.WriteVarint(128);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x80, 0x01 }));

            // 300 = 0x012C -> varint: [0xAC, 0x02]
            w.Reset();
            w.WriteVarint(300);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0xAC, 0x02 }));
        }

        [Test]
        public void WriteVarint_LargeValue()
        {
            var w = new ProtobufWriter();
            // 2^32 = 4294967296 -> 5 bytes
            w.WriteVarint(4294967296UL);
            var bytes = w.ToArray();
            Assert.That(bytes, Has.Length.EqualTo(5));
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 }));
        }

        [Test]
        public void WriteTag_EncodesFieldNumberAndWireType()
        {
            var w = new ProtobufWriter();

            // field 1, wire type 0 (varint) -> tag = (1 << 3) | 0 = 0x08
            w.WriteTag(1, 0);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x08 }));

            // field 2, wire type 2 (length-delimited) -> tag = (2 << 3) | 2 = 0x12
            w.Reset();
            w.WriteTag(2, 2);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x12 }));

            // field 8, wire type 5 (32-bit) -> tag = (8 << 3) | 5 = 0x45
            w.Reset();
            w.WriteTag(8, 5);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x45 }));
        }

        [Test]
        public void WriteString_NonEmpty_WritesTagLengthAndUTF8()
        {
            var w = new ProtobufWriter();
            // field 1, string "hi" -> tag 0x0A, length 2, bytes 0x68 0x69
            w.WriteString(1, "hi");
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x0A, 0x02, 0x68, 0x69 }));
        }

        [Test]
        public void WriteString_EmptyOrNull_Skipped()
        {
            var w = new ProtobufWriter();

            w.WriteString(1, "");
            Assert.That(w.Length, Is.EqualTo(0));

            w.WriteString(1, null);
            Assert.That(w.Length, Is.EqualTo(0));
        }

        [Test]
        public void WriteString_ASCII()
        {
            var w = new ProtobufWriter();
            w.WriteString(1, "ABC");
            var bytes = w.ToArray();
            // tag (0x0A) + length (3) + "ABC"
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x0A, 0x03, 0x41, 0x42, 0x43 }));
        }

        [Test]
        public void WriteString_MultiByte_UTF8()
        {
            var w = new ProtobufWriter();
            // CJK characters: 5 chars, 15 UTF-8 bytes (3 bytes per char)
            w.WriteString(1, "\u3053\u3093\u306B\u3061\u306F");
            var bytes = w.ToArray();
            // tag (0x0A) + length (0x0F = 15) + 15 payload bytes = 17 total
            Assert.That(bytes[0], Is.EqualTo(0x0A));
            Assert.That(bytes[1], Is.EqualTo(15));
            Assert.That(bytes.Length, Is.EqualTo(17));
            var decoded = Encoding.UTF8.GetString(bytes, 2, 15);
            Assert.That(decoded, Is.EqualTo("\u3053\u3093\u306B\u3061\u306F"));
        }

        [Test]
        public void WriteString_LargeString_ExpandsBuffer()
        {
            var w = new ProtobufWriter();
            // String > 256 bytes triggers _utf8Buffer reallocation
            var longStr = new string('X', 300);
            w.WriteString(1, longStr);
            var bytes = w.ToArray();
            // tag (1) + length varint (2 bytes for 300) + 300 payload = 303
            Assert.That(bytes.Length, Is.EqualTo(303));
        }

        [Test]
        public void WriteInt64_NonZero_WritesVarint()
        {
            var w = new ProtobufWriter();
            // field 2, value 1000 -> tag 0x10, varint(1000) = [0xE8, 0x07]
            w.WriteInt64(2, 1000L);
            Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0x10, 0xE8, 0x07 }));
        }

        [Test]
        public void WriteInt64_Negative_Encodes10ByteVarint()
        {
            var w = new ProtobufWriter();
            // -1L cast to ulong = 0xFFFFFFFFFFFFFFFF -> 10-byte varint
            w.WriteInt64(2, -1L);
            var bytes = w.ToArray();
            // 1 byte tag + 10 bytes varint
            Assert.That(bytes.Length, Is.EqualTo(11));
            Assert.That(bytes[0], Is.EqualTo(0x10)); // tag
        }

        [Test]
        public void WriteInt64_Zero_Skipped()
        {
            var w = new ProtobufWriter();
            w.WriteInt64(2, 0L);
            Assert.That(w.Length, Is.EqualTo(0));
        }

        [Test]
        public void WriteFloat_NonZero_WritesFixed32()
        {
            var w = new ProtobufWriter();
            // field 8, value 60.0f
            w.WriteFloat(8, 60.0f);
            var bytes = w.ToArray();
            // tag: (8<<3)|5 = 0x45
            Assert.That(bytes[0], Is.EqualTo(0x45));
            // Total: 1 byte tag + 4 bytes fixed32
            Assert.That(bytes.Length, Is.EqualTo(5));
            // Verify IEEE 754: 60.0f = 0x42700000 little-endian: 00 00 70 42
            Assert.That(bytes[1], Is.EqualTo(0x00));
            Assert.That(bytes[2], Is.EqualTo(0x00));
            Assert.That(bytes[3], Is.EqualTo(0x70));
            Assert.That(bytes[4], Is.EqualTo(0x42));
        }

        [Test]
        public void WriteFloat_Zero_Skipped()
        {
            var w = new ProtobufWriter();
            w.WriteFloat(8, 0f);
            Assert.That(w.Length, Is.EqualTo(0));
        }

        [Test]
        public void WriteDouble_NonZero_WritesFixed64()
        {
            var w = new ProtobufWriter();
            // field 13, value 3.14
            w.WriteDouble(13, 3.14);
            var bytes = w.ToArray();
            // tag: (13<<3)|1 = 0x69
            Assert.That(bytes[0], Is.EqualTo(0x69));
            // Total: 1 byte tag + 8 bytes fixed64
            Assert.That(bytes.Length, Is.EqualTo(9));
            // Verify IEEE 754: 3.14 = 0x40091EB851EB851F little-endian
            var expected = BitConverter.GetBytes(3.14);
            for (int i = 0; i < 8; i++)
                Assert.That(bytes[1 + i], Is.EqualTo(expected[i]));
        }

        [Test]
        public void WriteDouble_Zero_Skipped()
        {
            var w = new ProtobufWriter();
            w.WriteDouble(13, 0.0);
            Assert.That(w.Length, Is.EqualTo(0));
        }

        [Test]
        public void WriteEnum_NonZero_WritesVarint()
        {
            var w = new ProtobufWriter();
            // field 14, value 2 (Automated)
            w.WriteEnum(14, 2);
            var bytes = w.ToArray();
            // tag: (14<<3)|0 = 0x70
            Assert.That(bytes[0], Is.EqualTo(0x70));
            Assert.That(bytes[1], Is.EqualTo(0x02));
        }

        [Test]
        public void WriteEnum_Zero_Skipped()
        {
            var w = new ProtobufWriter();
            w.WriteEnum(14, 0);
            Assert.That(w.Length, Is.EqualTo(0));
        }

        [Test]
        public void WriteSubMessage_WritesNestedBytes()
        {
            var outer = new ProtobufWriter();
            var inner = new ProtobufWriter();

            inner.WriteFloat(1, 1.0f);
            inner.WriteFloat(2, 2.0f);

            outer.WriteSubMessage(5, inner);
            var bytes = outer.ToArray();

            // tag: (5<<3)|2 = 0x2A
            Assert.That(bytes[0], Is.EqualTo(0x2A));
            // length: inner has 2 floats * 5 bytes each = 10 bytes
            Assert.That(bytes[1], Is.EqualTo(10));
        }

        [Test]
        public void WriteSubMessage_EmptyInner_Skipped()
        {
            var outer = new ProtobufWriter();
            var inner = new ProtobufWriter();

            outer.WriteSubMessage(5, inner);
            Assert.That(outer.Length, Is.EqualTo(0));
        }

        [Test]
        public void Reset_ClearsWriter()
        {
            var w = new ProtobufWriter();
            w.WriteString(1, "hello");
            Assert.That(w.Length, Is.GreaterThan(0));

            w.Reset();
            Assert.That(w.Length, Is.EqualTo(0));
            Assert.That(w.ToArray(), Is.Empty);
        }

        [Test]
        public void DisposedWriter_MethodsDoNotThrow()
        {
            var w = new ProtobufWriter();
            w.WriteString(1, "hello");
            w.Dispose();

            Assert.DoesNotThrow(() => w.Reset());
            Assert.DoesNotThrow(() => w.WriteString(1, "ignored"));
            Assert.DoesNotThrow(() => w.WriteVarint(1));
            Assert.DoesNotThrow(() => w.WriteRawBytes(new byte[] { 1, 2, 3 }, 0, 3));
            Assert.That(w.ToArray(), Is.Empty);
        }

        [Test]
        public void WriteRawBytes_InvalidRange_IsNoOp()
        {
            var w = new ProtobufWriter();
            w.WriteString(1, "prefix");
            var before = w.ToArray();

            Assert.DoesNotThrow(() => w.WriteRawBytes(new byte[] { 1, 2, 3 }, -1, 1));
            Assert.DoesNotThrow(() => w.WriteRawBytes(new byte[] { 1, 2, 3 }, 0, 4));
            Assert.DoesNotThrow(() => w.WriteRawBytes(null, 0, 1));

            Assert.That(w.ToArray(), Is.EqualTo(before));
        }

        [Test]
        public void WriteFloatPresent_Zero_EmitsExactly6Bytes()
        {
            // WriteFloatPresent(18, 0f) must emit tag + 4 zero bytes.
            // tag for field 18, wire type 5 (32-bit): raw tag value = (18<<3)|5 = 149.
            // 149 > 127 so it needs a 2-byte varint: [0x95, 0x01].
            // Total: 2 (tag varint) + 4 (fixed32) = 6 bytes.
            // 0f IEEE 754 = 0x00000000 little-endian -> 4 zero bytes.
            var w = new ProtobufWriter();
            w.WriteFloatPresent(18, 0f);
            var bytes = w.ToArray();

            Assert.That(bytes.Length, Is.EqualTo(6), "tag varint (2) + fixed32 (4) = 6 bytes");
            Assert.That(bytes[0], Is.EqualTo(0x95), "first varint byte of tag for field 18 wire type 5");
            Assert.That(bytes[1], Is.EqualTo(0x01), "second varint byte of tag for field 18 wire type 5");
            Assert.That(bytes[2], Is.EqualTo(0x00));
            Assert.That(bytes[3], Is.EqualTo(0x00));
            Assert.That(bytes[4], Is.EqualTo(0x00));
            Assert.That(bytes[5], Is.EqualTo(0x00));
        }

        [Test]
        public void WriteFloatPresent_NonZero_RoundTrips()
        {
            // Write 45.0f on field 19, read back the IEEE 754 bytes.
            // tag for field 19, wire type 5: raw value = (19<<3)|5 = 157.
            // 157 > 127 so it needs a 2-byte varint: [0x9D, 0x01].
            // Total: 2 (tag varint) + 4 (fixed32) = 6 bytes.
            var w = new ProtobufWriter();
            w.WriteFloatPresent(19, 45.0f);
            var bytes = w.ToArray();

            Assert.That(bytes.Length, Is.EqualTo(6), "tag varint (2) + fixed32 (4) = 6 bytes");
            Assert.That(bytes[0], Is.EqualTo(0x9D), "first varint byte of tag for field 19 wire type 5");
            Assert.That(bytes[1], Is.EqualTo(0x01), "second varint byte of tag for field 19 wire type 5");

            // protobuf fixed32 is always little-endian; derive the expected bytes
            // from the IEEE 754 bit pattern via shifts so the test is endian-independent.
            int bits = BitConverter.SingleToInt32Bits(45.0f);
            for (int i = 0; i < 4; i++)
                Assert.That(bytes[2 + i], Is.EqualTo((byte)((bits >> (8 * i)) & 0xFF)), $"little-endian byte {i} of 45.0f");
        }
    }
}
