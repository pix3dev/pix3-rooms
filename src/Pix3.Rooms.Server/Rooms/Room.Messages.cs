using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Inbound message handling. Everything here runs on the room's tick thread, inside the drain phase,
/// with the sender already resolved to a member — nothing reads an identity out of a payload.
/// </summary>
public sealed partial class Room
{
    private const long ChatWindowSeconds = 60L;

    /// <summary>Routes one decoded frame by TypeId.</summary>
    private void Handle(RoomMember member, in InboundMessage message, uint tick)
    {
        switch (message.TypeId)
        {
            // Hot plane first: this is the frame that arrives per client per tick.
            case MessageTypeIds.EntityUpdateFrame:
                HandleEntityUpdateFrame(member, in message);
                break;
            case MessageTypeIds.EntitySpawnRequest:
                HandleEntitySpawn(member, in message);
                break;
            case MessageTypeIds.EntityDespawnRequest:
                HandleEntityDespawn(member, in message);
                break;
            case MessageTypeIds.SetEntityColdPropsRequest:
                HandleSetEntityColdProps(member, in message);
                break;
            case MessageTypeIds.ChatMessageRequest:
                HandleChatMessage(member, in message);
                break;
            case MessageTypeIds.SetRoomVarRequest:
                HandleSetRoomVar(member, in message);
                break;
            case MessageTypeIds.RemoteEventRequest:
                HandleRemoteEvent(member, in message);
                break;
            case MessageTypeIds.LeaveRequest:
                // Empty payload: the TypeId is the whole message, so there is nothing to deserialize.
                HandleLeaveRequest(member);
                break;
            case MessageTypeIds.PingRequest:
                HandlePing(member, in message, tick);
                break;
            default:
                HandleUnroutable(member, message.TypeId);
                break;
        }
    }

    // ── Hot plane: client entity updates ──────────────────────────────────────

    /// <summary>
    /// Applies a client's <c>EntityUpdateFrame</c>: every record must pass mask legality, finiteness and
    /// ownership. The client's tick is advisory — the server stamps its own — and the sender's own
    /// entity doubles as its area-of-interest centre.
    /// </summary>
    private void HandleEntityUpdateFrame(RoomMember member, in InboundMessage message)
    {
        if (!HotWire.TryReadEntityUpdateFrame(message.Frame, out uint clientTick, out int count, out ReadOnlySpan<byte> records))
        {
            _malformedMessages++;
            return;
        }

        _ = clientTick; // Advisory only: never trusted for ordering that affects other clients.

        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            if (!HotWire.TryReadDeltaRecord(
                    records.Slice(offset),
                    out uint netId,
                    out byte mask,
                    out EntityWireState state,
                    out int bytesRead))
            {
                // The frame lied about its record count or a record was truncated.
                _malformedMessages++;
                return;
            }

            offset += bytesRead;

            if (!HotWire.IsClientMaskLegal(mask))
            {
                _illegalMaskRecords++;
                continue;
            }

            if (!IsFiniteForMask(mask, in state))
            {
                _nonFiniteRecords++;
                continue;
            }

            if (!_replication.TryApplyOwnedUpdate(netId, member.ClientId, mask, in state))
            {
                _ownershipViolations++;
                continue;
            }

            if (netId != member.FocusNetId)
            {
                continue;
            }

            if ((mask & DeltaMask.X) != 0)
            {
                member.FocusX = state.X;
                member.FocusDirty = true;
            }

            if ((mask & DeltaMask.Y) != 0)
            {
                member.FocusY = state.Y;
                member.FocusDirty = true;
            }
        }
    }

    /// <summary>
    /// True when every float the mask claims is finite. NaN/±∞ coordinates would corrupt the spatial
    /// hash for every other player in the room, so such a record is dropped rather than applied.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFiniteForMask(byte mask, in EntityWireState state)
    {
        if ((mask & DeltaMask.X) != 0 && !float.IsFinite(state.X))
        {
            return false;
        }

        if ((mask & DeltaMask.Y) != 0 && !float.IsFinite(state.Y))
        {
            return false;
        }

        if ((mask & DeltaMask.Rot) != 0 && !float.IsFinite(state.Rot))
        {
            return false;
        }

        if ((mask & DeltaMask.Vx) != 0 && !float.IsFinite(state.Vx))
        {
            return false;
        }

        if ((mask & DeltaMask.Vy) != 0 && !float.IsFinite(state.Vy))
        {
            return false;
        }

        return true;
    }

    // ── Entity lifecycle ──────────────────────────────────────────────────────

    private void HandleEntitySpawn(RoomMember member, in InboundMessage message)
    {
        EntitySpawnRequest? request = Deserialize<EntitySpawnRequest>(in message);
        if (request is null)
        {
            return;
        }

        RejectCode reject = RejectCode.None;
        uint netId = NetId.None;
        bool spawned = false;

        if (!float.IsFinite(request.X) || !float.IsFinite(request.Y) || !float.IsFinite(request.Rot))
        {
            reject = RejectCode.BadRequest;
            _nonFiniteRecords++;
        }
        else if (member.OwnedEntityCount >= _options.MaxEntitiesPerOwner)
        {
            reject = RejectCode.QuotaExceeded;
        }
        else
        {
            EntityWireState state = default;
            state.X = request.X;
            state.Y = request.Y;
            state.Rot = request.Rot;
            state.Kind = request.Kind;
            state.OwnerId = member.ClientId;

            spawned = _replication.TrySpawn(member.ClientId, request.Kind, in state, out netId, out reject);
            if (spawned)
            {
                _entities[netId] = new EntityInfo(member.ClientId, null);
                member.OwnedEntityCount++;

                // The first entity a client spawns is, by convention, its avatar: it drives that
                // client's area of interest until it despawns.
                if (member.FocusNetId == NetId.None)
                {
                    member.FocusNetId = netId;
                    member.FocusX = state.X;
                    member.FocusY = state.Y;
                    member.FocusDirty = true;
                }
            }
        }

        _spawnAckScratch.RequestId = request.RequestId;
        _spawnAckScratch.NetId = spawned ? netId : NetId.None;
        _spawnAckScratch.RejectCode = (ushort)(spawned ? RejectCode.None : reject);
        SendTo(member.ClientId, MessageTypeIds.EntitySpawnAckEvent, _spawnAckScratch);

        if (!spawned)
        {
            _spawnRejections++;
            _logger.LogDebug(
                "Room {RoomId} refused a spawn from client {ClientId}: {Reject}",
                _config.RoomId, member.ClientId, reject);
            return;
        }

        byte[]? coldProps = request.ColdProps;
        if (coldProps is { Length: > 0 })
        {
            StoreAndFanOutColdProps(member.ClientId, netId, coldProps);
        }
    }

    private void HandleEntityDespawn(RoomMember member, in InboundMessage message)
    {
        EntityDespawnRequest? request = Deserialize<EntityDespawnRequest>(in message);
        if (request is null)
        {
            return;
        }

        uint netId = request.NetId;
        if (!_replication.TryDespawn(netId, member.ClientId, out RejectCode reject))
        {
            _ownershipViolations++;
            _logger.LogDebug(
                "Room {RoomId} refused a despawn of {NetId} from client {ClientId}: {Reject}",
                _config.RoomId, netId, member.ClientId, reject);
            return;
        }

        if (_entities.Remove(netId) && member.OwnedEntityCount > 0)
        {
            member.OwnedEntityCount--;
        }

        if (member.FocusNetId == netId)
        {
            // The avatar is gone; the next entity this client spawns takes over as its AOI centre.
            member.FocusNetId = NetId.None;
        }
    }

    private void HandleSetEntityColdProps(RoomMember member, in InboundMessage message)
    {
        SetEntityColdPropsRequest? request = Deserialize<SetEntityColdPropsRequest>(in message);
        if (request is null)
        {
            return;
        }

        byte[]? json = request.Json;
        if (json is null)
        {
            _coldPropsRejections++;
            return;
        }

        ref EntityInfo entity = ref CollectionsMarshal.GetValueRefOrNullRef(_entities, request.NetId);
        if (Unsafe.IsNullRef(ref entity) || entity.OwnerId != member.ClientId)
        {
            _ownershipViolations++;
            return;
        }

        StoreAndFanOutColdProps(member.ClientId, request.NetId, json);
    }

    /// <summary>
    /// Stores an entity's cold props and tells the other members about them.
    /// </summary>
    /// <remarks>
    /// <b>v0 simplification.</b> The spec wants this delivered to the AOI-visible peers only, but
    /// <see cref="Replication.IRoomReplication"/> exposes no "who can see this entity" query, so the
    /// event goes to every member of the room (never outside it). Cold props are a low-rate control
    /// message, so the cost is bounded; narrowing it to the AOI set needs a replication-side API and is
    /// a deliberate follow-up. Peers that enter the AOI later are covered the same way: they get the
    /// value when it next changes. The stored copy is what a future AOI-enter hook will serve.
    /// </remarks>
    private void StoreAndFanOutColdProps(uint ownerId, uint netId, byte[] json)
    {
        if (json.Length > _options.MaxColdPropsBytes)
        {
            _coldPropsRejections++;
            _logger.LogDebug(
                "Room {RoomId} refused {ByteCount} bytes of cold props for {NetId} (cap {Cap})",
                _config.RoomId, json.Length, netId, _options.MaxColdPropsBytes);
            return;
        }

        ref EntityInfo entity = ref CollectionsMarshal.GetValueRefOrNullRef(_entities, netId);
        if (Unsafe.IsNullRef(ref entity))
        {
            _coldPropsRejections++;
            return;
        }

        entity.ColdProps = json;

        _coldPropsScratch.NetId = netId;
        _coldPropsScratch.Json = json;
        BroadcastControlExcept(MessageTypeIds.EntityColdPropsEvent, _coldPropsScratch, ownerId);
        _coldPropsScratch.Json = []; // Don't pin a client payload inside the reusable scratch message.
    }

    // ── Chat ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sanitises, rate-limits and fans out a chat message <b>to this room only</b> — the reference
    /// server's global broadcast is exactly the bug this fabric exists to avoid.
    /// </summary>
    private void HandleChatMessage(RoomMember member, in InboundMessage message)
    {
        ChatMessageRequest? request = Deserialize<ChatMessageRequest>(in message);
        if (request is null)
        {
            return;
        }

        if (!TryConsumeChatAllowance(member))
        {
            _chatThrottled++;
            return;
        }

        string text = RoomText.Sanitize(request.Text, _options.MaxChatLength);
        if (text.Length == 0)
        {
            return;
        }

        _chatScratch.ClientId = member.ClientId;
        _chatScratch.Text = text;
        BroadcastControl(MessageTypeIds.ChatMessageEvent, _chatScratch);
        _chatScratch.Text = "";
    }

    /// <summary>Fixed 60-second window per member; cheaper than a sliding window and good enough.</summary>
    private bool TryConsumeChatAllowance(RoomMember member)
    {
        long now = Stopwatch.GetTimestamp();
        long window = Stopwatch.Frequency * ChatWindowSeconds;

        if (member.ChatWindowStart == 0L || now - member.ChatWindowStart >= window)
        {
            member.ChatWindowStart = now;
            member.ChatCountInWindow = 0;
        }

        if (member.ChatCountInWindow >= _options.MaxChatPerMinute)
        {
            return false;
        }

        member.ChatCountInWindow++;
        return true;
    }

    // ── Room vars ─────────────────────────────────────────────────────────────

    private void HandleSetRoomVar(RoomMember member, in InboundMessage message)
    {
        SetRoomVarRequest? request = Deserialize<SetRoomVarRequest>(in message);
        if (request is null)
        {
            return;
        }

        if (_options.RestrictRoomVarsToHost && member.ClientId != Volatile.Read(ref _hostClientId))
        {
            // No wire code exists for "not the host" that isn't also a close reason, so the write is
            // dropped and counted rather than tearing the session down.
            _roomVarRejections++;
            return;
        }

        string key = RoomText.Sanitize(request.Key, _options.MaxRoomVarKeyLength);
        if (key.Length == 0)
        {
            _roomVarRejections++;
            return;
        }

        byte[]? value = request.Value;
        if (value is null || value.Length > _options.MaxRoomVarValueBytes)
        {
            _roomVarRejections++;
            return;
        }

        if (!_roomVars.ContainsKey(key) && _roomVars.Count >= _options.MaxRoomVars)
        {
            _roomVarRejections++;
            _logger.LogDebug(
                "Room {RoomId} refused room var '{Key}': the room already holds {Count} keys",
                _config.RoomId, key, _roomVars.Count);
            return;
        }

        _roomVars[key] = value;

        // "Changed subset afterwards": one key per event.
        _roomVarKeyScratch[0] = key;
        _roomVarValueScratch[0] = value;
        _roomVarsScratch.Keys = _roomVarKeyScratch;
        _roomVarsScratch.Values = _roomVarValueScratch;
        BroadcastControl(MessageTypeIds.RoomVarsEvent, _roomVarsScratch);
        _roomVarValueScratch[0] = [];
    }

    /// <summary>Sends the complete room-var set to a joiner, as the handshake sequence requires.</summary>
    private void SendFullRoomVars(RoomMember member)
    {
        int count = _roomVars.Count;
        var keys = new string[count];
        var values = new byte[count][];

        int index = 0;
        foreach (KeyValuePair<string, byte[]> pair in _roomVars)
        {
            keys[index] = pair.Key;
            values[index] = pair.Value;
            index++;
        }

        // A joiner is rare enough to deserve its own event instance rather than the shared scratch.
        var full = new RoomVarsEvent { Keys = keys, Values = values };
        SendTo(member.ClientId, MessageTypeIds.RoomVarsEvent, full);
    }

    // ── Remote events ─────────────────────────────────────────────────────────

    private void HandleRemoteEvent(RoomMember member, in InboundMessage message)
    {
        RemoteEventRequest? request = Deserialize<RemoteEventRequest>(in message);
        if (request is null)
        {
            return;
        }

        string name = RoomText.Sanitize(request.Name, _options.MaxRemoteEventNameLength);
        if (name.Length == 0)
        {
            _remoteEventRejections++;
            return;
        }

        byte[] payload = request.Payload ?? [];
        if (payload.Length > _options.MaxRemoteEventPayloadBytes)
        {
            _remoteEventRejections++;
            return;
        }

        var target = (RemoteEventTarget)request.Target;
        if (target == RemoteEventTarget.Server)
        {
            // Relay rooms hold no server-side game logic, so there is nobody to deliver this to.
            // Counted (not silently ignored) because it usually means a mis-targeted client call.
            _serverTargetedRemoteEvents++;
            return;
        }

        if (target is not (RemoteEventTarget.AllPeers or RemoteEventTarget.SinglePeer or RemoteEventTarget.AoiPeers))
        {
            _remoteEventRejections++;
            return;
        }

        _remoteEventScratch.SenderClientId = member.ClientId;
        _remoteEventScratch.Name = name;
        _remoteEventScratch.Payload = payload;

        if (target == RemoteEventTarget.SinglePeer)
        {
            SendTo(request.TargetClientId, MessageTypeIds.RemoteEventBroadcast, _remoteEventScratch);
        }
        else
        {
            // v0 simplification: AoiPeers is served as AllPeers because IRoomReplication exposes no
            // per-subscriber visibility query. Still strictly room-scoped, never a global broadcast.
            BroadcastControlExcept(MessageTypeIds.RemoteEventBroadcast, _remoteEventScratch, member.ClientId);
        }

        _remoteEventScratch.Payload = [];
    }

    // ── Session ───────────────────────────────────────────────────────────────

    private void HandleLeaveRequest(RoomMember member)
    {
        Leave(member.ClientId, LeaveReason.LeftVoluntarily);
        member.Connection.RequestClose(RejectCode.None, "left the room");
    }

    /// <summary>
    /// Answers a ping. The transport can also answer this; a room handles it so that a forwarded ping is
    /// never silently dropped, and because the room is the only holder of the authoritative server tick.
    /// </summary>
    private void HandlePing(RoomMember member, in InboundMessage message, uint tick)
    {
        PingRequest? request = Deserialize<PingRequest>(in message);
        if (request is null)
        {
            return;
        }

        _pongScratch.ClientTimeMs = request.ClientTimeMs;
        _pongScratch.ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _pongScratch.ServerTick = tick;
        SendTo(member.ClientId, MessageTypeIds.PongEvent, _pongScratch);
    }

    private void HandleUnroutable(RoomMember member, byte typeId)
    {
        _unroutableMessages++;
        _logger.LogDebug(
            "Room {RoomId} ignored {MessageName} ({TypeId}) from client {ClientId}: the room does not route it",
            _config.RoomId, MessageTypeIds.GetName(typeId), typeId, member.ClientId);
    }

    /// <summary>
    /// MemoryPack-decodes a control payload. Returns null (and counts) for a payload that decodes to
    /// nothing; a malformed payload throws and is caught, counted and logged by the drain loop.
    /// </summary>
    private T? Deserialize<T>(in InboundMessage message) where T : class
    {
        T? value = MemoryPackSerializer.Deserialize<T>(message.Body);
        if (value is null)
        {
            _malformedMessages++;
        }

        return value;
    }
}
