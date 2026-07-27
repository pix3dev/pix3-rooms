using System.Reflection;
using MemoryPack;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Tests.Protocol;

/// <summary>
/// Contract tests for the control plane: the MemoryPack-serialized classes behind TypeIds 1–16, 64–66,
/// 70–71, 128–129.
/// </summary>
/// <remarks>
/// <para>
/// The hot plane is pinned byte for byte in <see cref="HotWireGoldenVectorTests"/>. The control plane
/// deliberately is not: its whole point is that a field can be appended without a version bump, so what
/// has to be guarded is the <i>property that makes that safe</i> — every message version-tolerant, every
/// member explicitly ordered. Retrofitting either later is itself a wire break, which is why v2 shipped
/// with both and why this test exists rather than a byte vector.
/// </para>
/// <para>
/// The naming rule is enforced here too: "a <c>MessageTypeIds</c> constant is spelled exactly like its
/// class, so one grep finds a message's whole path".
/// </para>
/// </remarks>
public class ControlMessageContractTests
{
    private static readonly Assembly ProtocolAssembly = typeof(HelloCommand).Assembly;

    private static IEnumerable<Type> MemoryPackableMessages()
        => ProtocolAssembly
            .GetTypes()
            .Where(t => t.GetCustomAttributesData()
                .Any(a => a.AttributeType.Name == "MemoryPackableAttribute"))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    public static TheoryData<string> MessageTypeNames()
    {
        TheoryData<string> data = [];
        foreach (Type type in MemoryPackableMessages())
        {
            data.Add(type.Name);
        }

        return data;
    }

    private static Type ResolveMessage(string name)
        => ProtocolAssembly.GetType($"Pix3.Rooms.Protocol.{name}")
           ?? throw new InvalidOperationException($"No control message type named {name}.");

    [Fact]
    public void The_control_plane_has_the_messages_the_spec_lists()
    {
        // 16 core + 5 MemoryPacked state (64, 65, 66, 70, 71) + 2 signal (128, 129). The hot-plane
        // payloads (67, 68, 69, 130) are hand-packed and deliberately have no class at all.
        Assert.Equal(23, MemoryPackableMessages().Count());
    }

    [Theory]
    [MemberData(nameof(MessageTypeNames))]
    public void Every_control_message_is_version_tolerant(string typeName)
    {
        Type type = ResolveMessage(typeName);
        CustomAttributeData attribute = type.GetCustomAttributesData()
            .Single(a => a.AttributeType.Name == "MemoryPackableAttribute");

        Assert.NotEmpty(attribute.ConstructorArguments);
        CustomAttributeTypedArgument generateType = attribute.ConstructorArguments[0];
        Assert.Equal("VersionTolerant", Enum.GetName(generateType.ArgumentType, generateType.Value!));
    }

    [Theory]
    [MemberData(nameof(MessageTypeNames))]
    public void Every_serialized_member_carries_an_explicit_contiguous_order(string typeName)
    {
        Type type = ResolveMessage(typeName);
        PropertyInfo[] members = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

        int[] orders = members
            .Select(p => p.GetCustomAttributesData()
                .SingleOrDefault(a => a.AttributeType.Name == "MemoryPackOrderAttribute"))
            .Select(a => a is null ? -1 : (int)Convert.ToInt32(a.ConstructorArguments[0].Value))
            .ToArray();

        for (int i = 0; i < members.Length; i++)
        {
            Assert.True(orders[i] >= 0, $"{typeName}.{members[i].Name} has no [MemoryPackOrder].");
        }

        // Contiguous from 0: a gap or a duplicate means somebody deleted a field instead of retiring it,
        // and version tolerance only holds while an old order number keeps meaning what it meant.
        Assert.Equal(Enumerable.Range(0, members.Length).ToArray(), orders.OrderBy(o => o).ToArray());
    }

    [Theory]
    [MemberData(nameof(MessageTypeNames))]
    public void Every_control_message_has_a_MessageTypeIds_constant_spelled_exactly_like_its_class(string typeName)
    {
        FieldInfo? constant = typeof(MessageTypeIds)
            .GetField(typeName, BindingFlags.Public | BindingFlags.Static);

        Assert.True(constant is not null, $"MessageTypeIds has no constant named {typeName}.");
        byte id = (byte)constant!.GetRawConstantValue()!;
        Assert.Equal(typeName, MessageTypeIds.GetName(id));
        Assert.False(MessageTypeIds.IsHotPlane(id), $"{typeName} is MemoryPacked but sits on a hot-plane TypeId.");
    }

    [Theory]
    [MemberData(nameof(MessageTypeNames))]
    public void Every_control_message_survives_a_round_trip_through_MemoryPack(string typeName)
    {
        Type type = ResolveMessage(typeName);
        object instance = Activator.CreateInstance(type)!;

        byte[] bytes = MemoryPackSerializer.Serialize(type, instance);
        object? restored = MemoryPackSerializer.Deserialize(type, bytes);

        Assert.NotNull(restored);
        Assert.IsType(type, restored);
    }

    [Fact]
    public void A_populated_HelloCommand_round_trips_every_field()
    {
        HelloCommand hello = new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Token = "dev:player-1:room-1",
            RoomId = "room-1",
            DisplayName = "Ada",
            Capabilities = 0x0003,
            ResumeKey = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16],
        };

        HelloCommand? restored = MemoryPackSerializer.Deserialize<HelloCommand>(
            MemoryPackSerializer.Serialize(hello));

        Assert.NotNull(restored);
        Assert.Equal(hello.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal(hello.Token, restored.Token);
        Assert.Equal(hello.RoomId, restored.RoomId);
        Assert.Equal(hello.DisplayName, restored.DisplayName);
        Assert.Equal(hello.Capabilities, restored.Capabilities);
        Assert.Equal(hello.ResumeKey, restored.ResumeKey);
    }

    [Fact]
    public void A_HelloCommand_without_a_resume_key_round_trips_as_null()
    {
        // A fresh join and a resume attempt differ only by this field being present.
        HelloCommand hello = new() { ProtocolVersion = 2, Token = "t", RoomId = "r", DisplayName = "d" };

        HelloCommand? restored = MemoryPackSerializer.Deserialize<HelloCommand>(
            MemoryPackSerializer.Serialize(hello));

        Assert.NotNull(restored);
        Assert.Null(restored.ResumeKey);
    }

    [Fact]
    public void A_WelcomeEvent_round_trips_the_fields_a_client_needs_to_configure_itself()
    {
        WelcomeEvent welcome = new()
        {
            ClientId = 7,
            RoomId = "room-1",
            TickHz = 20,
            ServerTimeMs = 1_700_000_000_000L,
            ServerTick = 1234,
            AoiRadius = 1200f,
            MaxPlayers = 64,
            ProtocolVersion = 2,
            WorldOriginX = -2048f,
            WorldOriginY = -2048f,
            WorldSize = 4096f,
            Mode = 0,
            MaxVisibleEntities = 64,
            HostClientId = 7,
            ResumeKey = new byte[16],
            Resumed = true,
        };

        WelcomeEvent? restored = MemoryPackSerializer.Deserialize<WelcomeEvent>(
            MemoryPackSerializer.Serialize(welcome));

        Assert.NotNull(restored);
        Assert.Equal(7u, restored.ClientId);
        Assert.Equal(20, restored.TickHz);
        Assert.Equal(1234u, restored.ServerTick);
        Assert.Equal(1200f, restored.AoiRadius);
        Assert.Equal(2, restored.ProtocolVersion);
        Assert.Equal(-2048f, restored.WorldOriginX);
        Assert.Equal(4096f, restored.WorldSize);
        Assert.Equal(64, restored.MaxVisibleEntities);
        Assert.Equal(16, restored.ResumeKey.Length);
        Assert.True(restored.Resumed);
    }
}
