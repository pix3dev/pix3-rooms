using System.Reflection;
using System.Text;
using System.Text.Json;
using MemoryPack;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Tests.Protocol;

/// <summary>
/// Checks this codec against <c>docs/protocol-vectors.json</c>, the golden-vector file every
/// implementation of the protocol shares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here computes an expectation.</b> Every quantized integer and every byte string is read
/// out of the published file, which was derived by hand from <c>docs/protocol.md</c>. The other side of
/// the bridge is a hand-written TypeScript client checked against the same file, so a disagreement
/// found here is a disagreement between the two implementations — never something to be "fixed" by
/// editing the fixture.
/// </para>
/// <para>
/// <see cref="HotWireGoldenVectorTests"/> and <see cref="QuantizationGoldenVectorTests"/> spell the
/// same contract out in C# literals. That duplication is deliberate: those pin the codec even if the
/// shared file is lost or mis-copied, while this one proves the file and the codec agree.
/// </para>
/// </remarks>
public class GoldenVectorFileTests
{
    /// <summary>
    /// A composite packet vector states its section counts but not the fields of the record it embeds:
    /// it names the standalone vector whose bytes it repeats, in <c>embedsUpdateRecord</c> or
    /// <c>embedsOwnerUpdateRecord</c>. Resolving the reference rather than restating slot and mask is
    /// what keeps the composite and the standalone vector from ever drifting apart.
    /// </summary>
    /// <remarks>
    /// A packet whose record sections are all empty embeds nothing and so carries no reference; the slot
    /// and mask it would have supplied are then unused, and the accessors below stand in for them.
    /// </remarks>
    private static JsonElement EmbeddedRecord(JsonElement vector, string referenceProperty)
        => vector.TryGetProperty(referenceProperty, out JsonElement reference)
            ? HotVector(reference.GetString()!)
            : default;

    private static ushort EmbeddedSlot(JsonElement record)
        => record.ValueKind == JsonValueKind.Object ? record.GetProperty("slot").GetUInt16() : (ushort)0;

    private static byte EmbeddedMask(JsonElement record)
        => record.ValueKind == JsonValueKind.Object ? record.GetProperty("mask").GetByte() : (byte)0;

    private static readonly JsonDocument VectorFile = JsonDocument.Parse(File.ReadAllText(LocateVectorFile()));

    private static readonly Assembly ProtocolAssembly = typeof(HelloCommand).Assembly;

    private static JsonElement Root => VectorFile.RootElement;

    /// <summary>
    /// Walks up from the test binary to the repository root. Keeps the fixture a single source of truth
    /// rather than a copy that can drift silently.
    /// </summary>
    private static string LocateVectorFile()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "docs", "protocol-vectors.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"docs/protocol-vectors.json not found above {AppContext.BaseDirectory}.");
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static string NormalizeHex(string hex)
        => string.Concat(hex.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static byte[] HexBytes(string hex) => Convert.FromHexString(NormalizeHex(hex));

    private static string VectorHex(JsonElement vector) => NormalizeHex(vector.GetProperty("hex").GetString()!);

    private static int OptionalInt(JsonElement vector, string name)
        => vector.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;

    // ── quantization ──────────────────────────────────────────────────────────

    private static JsonElement Quantization => Root.GetProperty("quantization");

    /// <summary>The room world the position vectors are quantized against.</summary>
    private static WorldQuantizer VectorWorld()
    {
        JsonElement world = Quantization.GetProperty("world");
        return new WorldQuantizer(
            world.GetProperty("originX").GetSingle(),
            world.GetProperty("originY").GetSingle(),
            world.GetProperty("size").GetSingle());
    }

    /// <summary>
    /// A vector's float input. <c>"input"</c> is a decimal the file guarantees is exactly representable
    /// in float32; <c>"bits"</c> is an explicit float32 bit pattern, used where a decimal literal would
    /// round differently in C# and JavaScript.
    /// </summary>
    private static float VectorInput(JsonElement vector)
    {
        if (vector.TryGetProperty("bits", out JsonElement bits))
        {
            uint pattern = Convert.ToUInt32(bits.GetString()!, 16);
            return BitConverter.Int32BitsToSingle(unchecked((int)pattern));
        }

        return (float)vector.GetProperty("input").GetDouble();
    }

    private static IEnumerable<JsonElement> QuantizationVectors(string section)
        => Quantization.GetProperty(section).EnumerateArray();

    private static JsonElement QuantizationVector(string section, string name)
        => QuantizationVectors(section).Single(v => v.GetProperty("name").GetString() == name);

    private static TheoryData<string> QuantizationNames(string section)
    {
        TheoryData<string> data = [];
        foreach (JsonElement vector in QuantizationVectors(section))
        {
            data.Add(vector.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> PositionVectorNames() => QuantizationNames("position");

    [Theory]
    [MemberData(nameof(PositionVectorNames))]
    public void Position_quantization_matches_the_published_vector(string name)
    {
        JsonElement vector = QuantizationVector("position", name);
        float input = VectorInput(vector);
        int expected = vector.GetProperty("q").GetInt32();
        WorldQuantizer world = VectorWorld();

        Assert.True(world.TryQuantizePosition(input, input, out ushort qx, out ushort qy));

        // Both axes share an origin in this world, so one vector pins both.
        Assert.Equal(expected, qx);
        Assert.Equal(expected, qy);
    }

    public static TheoryData<int> PositionDequantizeCases()
    {
        TheoryData<int> data = [];
        foreach (JsonElement vector in QuantizationVectors("positionDequantize"))
        {
            data.Add(vector.GetProperty("q").GetInt32());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PositionDequantizeCases))]
    public void Position_dequantization_matches_the_published_vector(int q)
    {
        JsonElement vector = QuantizationVectors("positionDequantize")
            .Single(v => v.GetProperty("q").GetInt32() == q);
        float expected = (float)vector.GetProperty("expect").GetDouble();
        WorldQuantizer world = VectorWorld();

        Assert.Equal(expected, world.DequantizeX((ushort)q));
        Assert.Equal(expected, world.DequantizeY((ushort)q));
    }

    public static TheoryData<string> RotationVectorNames() => QuantizationNames("rotation");

    [Theory]
    [MemberData(nameof(RotationVectorNames))]
    public void Rotation_quantization_matches_the_published_vector(string name)
    {
        JsonElement vector = QuantizationVector("rotation", name);
        int expected = vector.GetProperty("q").GetInt32();

        Assert.True(WorldQuantizer.TryQuantizeRotation(VectorInput(vector), out byte q));

        Assert.Equal(expected, q);
    }

    public static TheoryData<string> VelocityVectorNames() => QuantizationNames("velocity");

    [Theory]
    [MemberData(nameof(VelocityVectorNames))]
    public void Velocity_quantization_matches_the_published_vector(string name)
    {
        JsonElement vector = QuantizationVector("velocity", name);
        int expected = vector.GetProperty("q").GetInt32();

        Assert.True(WorldQuantizer.TryQuantizeVelocity(VectorInput(vector), out short q));

        Assert.Equal(expected, q);
    }

    public static TheoryData<string> NonFiniteInputs()
    {
        TheoryData<string> data = [];
        foreach (JsonElement input in Quantization.GetProperty("nonFinite").GetProperty("inputs").EnumerateArray())
        {
            data.Add(input.GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NonFiniteInputs))]
    public void Non_finite_input_is_refused_by_every_quantizer(string text)
    {
        // Spelled out rather than parsed, so a new spelling in the fixture fails loudly instead of
        // silently becoming some other number.
        float value = text switch
        {
            "NaN" => float.NaN,
            "Infinity" => float.PositiveInfinity,
            "-Infinity" => float.NegativeInfinity,
            _ => throw new InvalidOperationException($"Unrecognised non-finite input '{text}'."),
        };
        WorldQuantizer world = VectorWorld();

        Assert.False(world.TryQuantizePosition(value, 0f, out _, out _));
        Assert.False(world.TryQuantizePosition(0f, value, out _, out _));
        Assert.False(WorldQuantizer.TryQuantizeRotation(value, out _));
        Assert.False(WorldQuantizer.TryQuantizeVelocity(value, out _));
    }

    // ── hot ───────────────────────────────────────────────────────────────────

    private static JsonElement Hot => Root.GetProperty("hot");

    private static uint SampleNetId() => Hot.GetProperty("sample").GetProperty("netId").GetUInt32();

    /// <summary>The one entity every hot vector describes, read field by field out of the fixture.</summary>
    private static EntityWireState SampleState()
    {
        JsonElement sample = Hot.GetProperty("sample");
        return new EntityWireState
        {
            Kind = sample.GetProperty("kind").GetUInt16(),
            OwnerId = sample.GetProperty("ownerId").GetUInt32(),
            QX = sample.GetProperty("qx").GetUInt16(),
            QY = sample.GetProperty("qy").GetUInt16(),
            QRot = sample.GetProperty("qrot").GetByte(),
            QVx = sample.GetProperty("qvx").GetInt16(),
            QVy = sample.GetProperty("qvy").GetInt16(),
            Flags = sample.GetProperty("flags").GetByte(),
        };
    }

    private static JsonElement HotVector(string name)
        => Hot.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

    public static TheoryData<string> HotVectorNames()
    {
        TheoryData<string> data = [];
        foreach (JsonElement vector in Hot.GetProperty("vectors").EnumerateArray())
        {
            data.Add(vector.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HotVectorNames))]
    public void Hot_vector_encodes_to_the_published_bytes(string name)
    {
        JsonElement vector = HotVector(name);

        byte[] encoded = EncodeHotVector(vector);

        Assert.Equal(VectorHex(vector), Convert.ToHexString(encoded));
    }

    [Theory]
    [MemberData(nameof(HotVectorNames))]
    public void Hot_vector_decodes_back_to_its_published_fields(string name)
    {
        JsonElement vector = HotVector(name);

        // Decoding the published bytes, not our own output: a reader and a writer that share a mistake
        // would round-trip happily.
        DecodeHotVector(vector, HexBytes(vector.GetProperty("hex").GetString()!));
    }

    private static byte[] EncodeHotVector(JsonElement vector)
    {
        string name = vector.GetProperty("name").GetString()!;
        return name.Split('/')[0] switch
        {
            "FullRecord" => EncodeFullRecord(),
            "UpdateRecord" => EncodeUpdateRecord(vector),
            "OwnerUpdateRecord" => EncodeOwnerUpdateRecord(vector),
            "RemovedSlot" => EncodeRemovedSlot(vector),
            "SnapshotPacket" => EncodeSnapshotPacket(vector),
            "DeltaPacket" => EncodeDeltaPacket(vector),
            "EntityUpdatePacket" => EncodeEntityUpdatePacket(vector),
            "SignalBatchPacket" => EncodeSignalBatchPacket(vector),
            _ => throw new InvalidOperationException($"No encoder for hot vector '{name}'."),
        };
    }

    private static void DecodeHotVector(JsonElement vector, byte[] bytes)
    {
        string name = vector.GetProperty("name").GetString()!;
        switch (name.Split('/')[0])
        {
            case "FullRecord":
                DecodeFullRecord(bytes);
                break;
            case "UpdateRecord":
                DecodeUpdateRecord(vector, bytes);
                break;
            case "OwnerUpdateRecord":
                DecodeOwnerUpdateRecord(vector, bytes);
                break;
            case "RemovedSlot":
                DecodeRemovedSlot(vector, bytes);
                break;
            case "SnapshotPacket":
                DecodeSnapshotPacket(vector, bytes);
                break;
            case "DeltaPacket":
                DecodeDeltaPacket(vector, bytes);
                break;
            case "EntityUpdatePacket":
                DecodeEntityUpdatePacket(vector, bytes);
                break;
            case "SignalBatchPacket":
                DecodeSignalBatchPacket(vector, bytes);
                break;
            default:
                throw new InvalidOperationException($"No decoder for hot vector '{name}'.");
        }
    }

    private static byte[] EncodeFullRecord()
    {
        byte[] buffer = new byte[HotWire.FullRecordSize];

        Assert.Equal(HotWire.FullRecordSize, HotWire.WriteFullRecord(buffer, SampleNetId(), SampleState()));

        return buffer;
    }

    private static void DecodeFullRecord(byte[] bytes)
    {
        Assert.True(HotWire.TryReadFullRecord(bytes, out uint netId, out EntityWireState state));
        Assert.Equal(SampleNetId(), netId);
        AssertStateMatchesSample(state);
    }

    private static byte[] EncodeUpdateRecord(JsonElement vector)
    {
        ushort slot = vector.GetProperty("slot").GetUInt16();
        byte mask = vector.GetProperty("mask").GetByte();
        byte[] buffer = new byte[HotWire.MaxUpdateRecordSize];

        int written = HotWire.WriteUpdateRecord(buffer, slot, mask, SampleState());

        Assert.Equal(HotWire.UpdateRecordSize(mask), written);
        return buffer.AsSpan(0, written).ToArray();
    }

    private static void DecodeUpdateRecord(JsonElement vector, byte[] bytes)
    {
        Assert.True(
            HotWire.TryReadUpdateRecord(bytes, out ushort slot, out byte mask, out EntityWireState state, out int read));
        Assert.Equal(vector.GetProperty("slot").GetUInt16(), slot);
        Assert.Equal(vector.GetProperty("mask").GetByte(), mask);
        Assert.Equal(bytes.Length, read);
        AssertMaskedFieldsMatchSample(mask, state);
    }

    private static byte[] EncodeOwnerUpdateRecord(JsonElement vector)
    {
        byte mask = vector.GetProperty("mask").GetByte();
        byte[] buffer = new byte[HotWire.MaxOwnerUpdateRecordSize];

        int written = HotWire.WriteOwnerUpdateRecord(buffer, SampleNetId(), mask, SampleState());

        Assert.Equal(HotWire.OwnerUpdateRecordSize(mask), written);
        return buffer.AsSpan(0, written).ToArray();
    }

    private static void DecodeOwnerUpdateRecord(JsonElement vector, byte[] bytes)
    {
        Assert.True(
            HotWire.TryReadOwnerUpdateRecord(bytes, out uint netId, out byte mask, out EntityWireState state, out int read));
        Assert.Equal(SampleNetId(), netId);
        Assert.Equal(vector.GetProperty("mask").GetByte(), mask);
        Assert.Equal(bytes.Length, read);
        AssertMaskedFieldsMatchSample(mask, state);
    }

    private static byte[] EncodeRemovedSlot(JsonElement vector)
    {
        byte[] buffer = new byte[HotWire.RemovedSlotSize];

        Assert.Equal(
            HotWire.RemovedSlotSize,
            HotWire.WriteRemovedSlot(buffer, vector.GetProperty("slot").GetUInt16()));

        return buffer;
    }

    private static void DecodeRemovedSlot(JsonElement vector, byte[] bytes)
    {
        Assert.True(HotWire.TryReadRemovedSlot(bytes, out ushort slot));
        Assert.Equal(vector.GetProperty("slot").GetUInt16(), slot);
    }

    private static byte[] EncodeSnapshotPacket(JsonElement vector)
    {
        int count = vector.GetProperty("count").GetInt32();
        byte[] frame = new byte[HotWire.SnapshotPacketHeaderSize + (count * HotWire.FullRecordSize)];

        int offset = HotWire.WriteSnapshotPacketHeader(
            frame,
            vector.GetProperty("seq").GetUInt16(),
            vector.GetProperty("serverTick").GetUInt32());
        for (int i = 0; i < count; i++)
        {
            offset += HotWire.WriteFullRecord(frame.AsSpan(offset), SampleNetId(), SampleState());
        }

        Assert.True(HotWire.TryPatchSnapshotPacketCount(frame, count));
        Assert.True(HotWire.TryPatchSnapshotPacketFrameFlags(frame, vector.GetProperty("frameFlags").GetByte()));
        Assert.Equal(frame.Length, offset);
        return frame;
    }

    private static void DecodeSnapshotPacket(JsonElement vector, byte[] bytes)
    {
        Assert.True(
            HotWire.TryReadSnapshotPacket(
                bytes,
                out ushort seq,
                out uint serverTick,
                out byte frameFlags,
                out int count,
                out ReadOnlySpan<byte> records));

        Assert.Equal(vector.GetProperty("seq").GetUInt16(), seq);
        Assert.Equal(vector.GetProperty("serverTick").GetUInt32(), serverTick);
        Assert.Equal(vector.GetProperty("frameFlags").GetByte(), frameFlags);
        Assert.Equal(vector.GetProperty("count").GetInt32(), count);
        Assert.Equal(count * HotWire.FullRecordSize, records.Length);

        for (int i = 0; i < count; i++)
        {
            Assert.True(
                HotWire.TryReadFullRecord(
                    records.Slice(i * HotWire.FullRecordSize),
                    out uint netId,
                    out EntityWireState state));
            Assert.Equal(SampleNetId(), netId);
            AssertStateMatchesSample(state);
        }
    }

    private static ushort[] RemovedSlots(JsonElement vector)
        => vector.TryGetProperty("removedSlots", out JsonElement slots)
            ? slots.EnumerateArray().Select(s => s.GetUInt16()).ToArray()
            : [];

    private static byte[] EncodeDeltaPacket(JsonElement vector)
    {
        ushort[] removed = RemovedSlots(vector);
        int enterCount = OptionalInt(vector, "enterCount");
        int updateCount = OptionalInt(vector, "updateCount");

        JsonElement embedded = EmbeddedRecord(vector, "embedsUpdateRecord");
        ushort updateSlot = EmbeddedSlot(embedded);
        byte updateMask = EmbeddedMask(embedded);

        byte[] frame = new byte[
            HotWire.DeltaPacketFixedOverhead
            + (removed.Length * HotWire.RemovedSlotSize)
            + (enterCount * HotWire.FullRecordSize)
            + (updateCount * HotWire.UpdateRecordSize(updateMask))];

        int offset = HotWire.WriteDeltaPacketHeader(
            frame,
            vector.GetProperty("seq").GetUInt16(),
            vector.GetProperty("serverTick").GetUInt32());

        int removedCountOffset = offset;
        offset += HotWire.WriteSectionCountPlaceholder(frame.AsSpan(offset));
        foreach (ushort slot in removed)
        {
            offset += HotWire.WriteRemovedSlot(frame.AsSpan(offset), slot);
        }

        Assert.True(HotWire.TryPatchSectionCount(frame, removedCountOffset, removed.Length));

        int enterCountOffset = offset;
        offset += HotWire.WriteSectionCountPlaceholder(frame.AsSpan(offset));
        for (int i = 0; i < enterCount; i++)
        {
            offset += HotWire.WriteFullRecord(frame.AsSpan(offset), SampleNetId(), SampleState());
        }

        Assert.True(HotWire.TryPatchSectionCount(frame, enterCountOffset, enterCount));

        int updateCountOffset = offset;
        offset += HotWire.WriteSectionCountPlaceholder(frame.AsSpan(offset));
        for (int i = 0; i < updateCount; i++)
        {
            offset += HotWire.WriteUpdateRecord(frame.AsSpan(offset), updateSlot, updateMask, SampleState());
        }

        Assert.True(HotWire.TryPatchSectionCount(frame, updateCountOffset, updateCount));
        Assert.Equal(frame.Length, offset);
        return frame;
    }

    private static void DecodeDeltaPacket(JsonElement vector, byte[] bytes)
    {
        Assert.True(HotWire.TryReadDeltaPacket(bytes, out DeltaPacketSections sections));

        Assert.Equal(vector.GetProperty("seq").GetUInt16(), sections.Seq);
        Assert.Equal(vector.GetProperty("serverTick").GetUInt32(), sections.ServerTick);

        ushort[] removed = RemovedSlots(vector);
        Assert.Equal(removed.Length, sections.RemovedCount);
        for (int i = 0; i < removed.Length; i++)
        {
            Assert.True(sections.TryGetRemovedSlot(i, out ushort slot));
            Assert.Equal(removed[i], slot);
        }

        int enterCount = OptionalInt(vector, "enterCount");
        Assert.Equal(enterCount, sections.EnterCount);
        for (int i = 0; i < enterCount; i++)
        {
            Assert.True(sections.TryGetEnterRecord(i, out uint netId, out EntityWireState entered));
            Assert.Equal(SampleNetId(), netId);
            AssertStateMatchesSample(entered);
        }

        JsonElement embedded = EmbeddedRecord(vector, "embedsUpdateRecord");
        int updateCount = OptionalInt(vector, "updateCount");
        Assert.Equal(updateCount, sections.UpdateCount);
        int cursor = 0;
        for (int i = 0; i < updateCount; i++)
        {
            Assert.True(
                sections.TryReadNextUpdate(ref cursor, out ushort slot, out byte mask, out EntityWireState updated));
            Assert.Equal(EmbeddedSlot(embedded), slot);
            Assert.Equal(EmbeddedMask(embedded), mask);
            AssertMaskedFieldsMatchSample(mask, updated);
        }

        Assert.Equal(sections.Updates.Length, cursor);
    }

    private static byte[] EncodeEntityUpdatePacket(JsonElement vector)
    {
        int count = vector.GetProperty("count").GetInt32();
        JsonElement embedded = EmbeddedRecord(vector, "embedsOwnerUpdateRecord");
        byte mask = EmbeddedMask(embedded);

        byte[] frame = new byte[
            HotWire.EntityUpdatePacketHeaderSize + (count * HotWire.OwnerUpdateRecordSize(mask))];

        int offset = HotWire.WriteEntityUpdatePacketHeader(frame, vector.GetProperty("clientTick").GetUInt32());
        for (int i = 0; i < count; i++)
        {
            offset += HotWire.WriteOwnerUpdateRecord(frame.AsSpan(offset), SampleNetId(), mask, SampleState());
        }

        Assert.True(HotWire.TryPatchEntityUpdatePacketCount(frame, count));
        Assert.Equal(frame.Length, offset);
        return frame;
    }

    private static void DecodeEntityUpdatePacket(JsonElement vector, byte[] bytes)
    {
        Assert.True(
            HotWire.TryReadEntityUpdatePacket(bytes, out uint clientTick, out int count, out ReadOnlySpan<byte> records));

        Assert.Equal(vector.GetProperty("clientTick").GetUInt32(), clientTick);
        Assert.Equal(vector.GetProperty("count").GetInt32(), count);

        JsonElement embedded = EmbeddedRecord(vector, "embedsOwnerUpdateRecord");
        int cursor = 0;
        for (int i = 0; i < count; i++)
        {
            Assert.True(
                HotWire.TryReadOwnerUpdateRecord(
                    records.Slice(cursor),
                    out uint netId,
                    out byte mask,
                    out EntityWireState state,
                    out int read));
            Assert.Equal(SampleNetId(), netId);
            Assert.Equal(EmbeddedMask(embedded), mask);
            AssertMaskedFieldsMatchSample(mask, state);
            cursor += read;
        }

        Assert.Equal(records.Length, cursor);
    }

    private static byte[] EncodeSignalBatchPacket(JsonElement vector)
    {
        JsonElement[] entries = vector.GetProperty("entries").EnumerateArray().ToArray();
        int size = HotWire.SignalBatchPacketHeaderSize;
        foreach (JsonElement entry in entries)
        {
            size += HotWire.SignalEntrySize(
                Encoding.UTF8.GetByteCount(entry.GetProperty("name").GetString()!),
                HexBytes(entry.GetProperty("payloadHex").GetString()!).Length);
        }

        byte[] frame = new byte[size];
        int offset = HotWire.WriteSignalBatchPacketHeader(
            frame,
            vector.GetProperty("seq").GetUInt16(),
            vector.GetProperty("serverTick").GetUInt32());

        foreach (JsonElement entry in entries)
        {
            offset += HotWire.WriteSignalEntry(
                frame.AsSpan(offset),
                entry.GetProperty("sender").GetUInt32(),
                Encoding.UTF8.GetBytes(entry.GetProperty("name").GetString()!),
                HexBytes(entry.GetProperty("payloadHex").GetString()!));
        }

        Assert.True(HotWire.TryPatchSignalBatchPacketCount(frame, entries.Length));
        Assert.Equal(frame.Length, offset);
        return frame;
    }

    private static void DecodeSignalBatchPacket(JsonElement vector, byte[] bytes)
    {
        Assert.True(HotWire.TryReadSignalBatchPacket(bytes, out SignalBatchSections sections));

        Assert.Equal(vector.GetProperty("seq").GetUInt16(), sections.Seq);
        Assert.Equal(vector.GetProperty("serverTick").GetUInt32(), sections.ServerTick);

        JsonElement[] entries = vector.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(entries.Length, sections.Count);

        int cursor = 0;
        foreach (JsonElement entry in entries)
        {
            Assert.True(
                sections.TryReadNextEntry(
                    ref cursor,
                    out uint sender,
                    out ReadOnlySpan<byte> name,
                    out ReadOnlySpan<byte> payload));
            Assert.Equal(entry.GetProperty("sender").GetUInt32(), sender);
            Assert.Equal(entry.GetProperty("name").GetString(), Encoding.UTF8.GetString(name));
            Assert.Equal(
                NormalizeHex(entry.GetProperty("payloadHex").GetString()!),
                Convert.ToHexString(payload));
        }

        Assert.Equal(sections.Entries.Length, cursor);
    }

    private static void AssertStateMatchesSample(in EntityWireState state)
    {
        EntityWireState sample = SampleState();
        Assert.Equal(sample.Kind, state.Kind);
        Assert.Equal(sample.OwnerId, state.OwnerId);
        Assert.Equal(sample.QX, state.QX);
        Assert.Equal(sample.QY, state.QY);
        Assert.Equal(sample.QRot, state.QRot);
        Assert.Equal(sample.QVx, state.QVx);
        Assert.Equal(sample.QVy, state.QVy);
        Assert.Equal(sample.Flags, state.Flags);
    }

    /// <summary>
    /// Masked fields must carry the sample's values and unmasked ones must stay at zero — that is what
    /// catches a decoder that reads the payload in the wrong order.
    /// </summary>
    private static void AssertMaskedFieldsMatchSample(byte mask, in EntityWireState state)
    {
        EntityWireState sample = SampleState();
        Assert.Equal((mask & DeltaMask.X) != 0 ? sample.QX : (ushort)0, state.QX);
        Assert.Equal((mask & DeltaMask.Y) != 0 ? sample.QY : (ushort)0, state.QY);
        Assert.Equal((mask & DeltaMask.Rot) != 0 ? sample.QRot : (byte)0, state.QRot);
        Assert.Equal((mask & DeltaMask.Vx) != 0 ? sample.QVx : (short)0, state.QVx);
        Assert.Equal((mask & DeltaMask.Vy) != 0 ? sample.QVy : (short)0, state.QVy);
        Assert.Equal((mask & DeltaMask.Flags) != 0 ? sample.Flags : (byte)0, state.Flags);
    }

    // ── control ───────────────────────────────────────────────────────────────

    private static JsonElement Control => Root.GetProperty("control");

    /// <summary>
    /// A vector's label: the message name, plus the <c>name</c> discriminator where one message has
    /// several vectors.
    /// </summary>
    private static string ControlVectorKey(JsonElement vector)
    {
        string message = vector.GetProperty("message").GetString()!;
        return vector.TryGetProperty("name", out JsonElement name)
            ? $"{message}/{name.GetString()}"
            : message;
    }

    private static JsonElement ControlVector(string key)
        => Control.GetProperty("vectors").EnumerateArray().Single(v => ControlVectorKey(v) == key);

    public static TheoryData<string> ControlVectorKeys()
    {
        TheoryData<string> data = [];
        foreach (JsonElement vector in Control.GetProperty("vectors").EnumerateArray())
        {
            data.Add(ControlVectorKey(vector));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ControlVectorKeys))]
    public void Control_vector_serializes_to_the_published_bytes(string key)
    {
        JsonElement vector = ControlVector(key);
        Type type = ResolveMessageType(vector);

        byte[] bytes = MemoryPackSerializer.Serialize(type, BuildControlMessage(type, vector.GetProperty("fields")));

        Assert.Equal(VectorHex(vector), Convert.ToHexString(bytes));
    }

    [Theory]
    [MemberData(nameof(ControlVectorKeys))]
    public void Control_vector_deserializes_back_to_its_published_fields(string key)
    {
        JsonElement vector = ControlVector(key);
        Type type = ResolveMessageType(vector);

        object? restored = MemoryPackSerializer.Deserialize(type, HexBytes(vector.GetProperty("hex").GetString()!));

        Assert.NotNull(restored);
        Assert.IsType(type, restored);
        foreach (JsonProperty field in vector.GetProperty("fields").EnumerateObject())
        {
            (string propertyName, object? expected) = ResolveControlField(type, field);
            object? actual = RequireProperty(type, propertyName).GetValue(restored);
            if (expected is null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.Equal(expected, actual);
            }
        }
    }

    private static Type ResolveMessageType(JsonElement vector)
    {
        string message = vector.GetProperty("message").GetString()!;
        return ProtocolAssembly.GetType($"Pix3.Rooms.Protocol.{message}")
               ?? throw new InvalidOperationException($"No control message type named {message}.");
    }

    private static PropertyInfo RequireProperty(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
           ?? throw new InvalidOperationException($"{type.Name} has no property named {name}.");

    private static object BuildControlMessage(Type type, JsonElement fields)
    {
        object instance = Activator.CreateInstance(type)!;
        foreach (JsonProperty field in fields.EnumerateObject())
        {
            (string propertyName, object? value) = ResolveControlField(type, field);
            RequireProperty(type, propertyName).SetValue(instance, value);
        }

        return instance;
    }

    /// <summary>
    /// Maps one JSON field onto its property and .NET value. The fixture's naming conventions:
    /// a <c>…Hex</c> key is a <c>byte[]</c> written as hex (<c>null</c> is a null array, <c>""</c> an
    /// empty one), <c>ValuesHex</c> is a <c>byte[][]</c>, and <c>…Repeat: {char, count}</c> is a string
    /// of one repeated character.
    /// </summary>
    private static (string PropertyName, object? Value) ResolveControlField(Type type, JsonProperty field)
    {
        if (field.Name == "ValuesHex")
        {
            byte[][] values = field.Value
                .EnumerateArray()
                .Select(v => HexBytes(v.GetString()!))
                .ToArray();
            return ("Values", values);
        }

        if (field.Name.EndsWith("Hex", StringComparison.Ordinal))
        {
            string property = field.Name[..^"Hex".Length];
            object? value = field.Value.ValueKind == JsonValueKind.Null
                ? null
                : HexBytes(field.Value.GetString()!);
            return (property, value);
        }

        if (field.Name.EndsWith("Repeat", StringComparison.Ordinal))
        {
            string property = field.Name[..^"Repeat".Length];
            string character = field.Value.GetProperty("char").GetString()!;
            int count = field.Value.GetProperty("count").GetInt32();
            return (property, new string(character.Single(), count));
        }

        return (field.Name, ConvertJsonValue(RequireProperty(type, field.Name).PropertyType, field.Value));
    }

    private static object ConvertJsonValue(Type target, JsonElement value)
    {
        if (target == typeof(string))
        {
            return value.GetString()!;
        }

        if (target == typeof(bool))
        {
            return value.GetBoolean();
        }

        if (target == typeof(byte))
        {
            return value.GetByte();
        }

        if (target == typeof(short))
        {
            return value.GetInt16();
        }

        if (target == typeof(ushort))
        {
            return value.GetUInt16();
        }

        if (target == typeof(int))
        {
            return value.GetInt32();
        }

        if (target == typeof(uint))
        {
            return value.GetUInt32();
        }

        if (target == typeof(long))
        {
            return value.GetInt64();
        }

        if (target == typeof(float))
        {
            return value.GetSingle();
        }

        if (target == typeof(string[]))
        {
            return value.EnumerateArray().Select(v => v.GetString()!).ToArray();
        }

        throw new InvalidOperationException($"No JSON conversion for property type {target.Name}.");
    }
}
