using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Inbound message handling. Everything here runs on the room's tick thread, inside the drain phase, with
/// the sender already resolved to a member — nothing reads an identity out of a payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the room validates, and what it does not.</b> <c>Net</c> owns the per-connection rates
/// (messages/s, bytes/s, payload size, records per packet, spawns/min, signal rates by target,
/// resyncs/s) and the structural decode; the room owns everything that needs room or entity state: the
/// entity-kind allowlist, cold-prop size and per-entity rate, entities per owner, room-var count/size and
/// the host-only restriction, chat rate and length, and the signal name/payload caps.
/// </para>
/// <para>
/// <b>There is no finiteness check left on the entity path.</b> Spawn and update carry <i>quantized</i>
/// integers, and a <c>u16 QX</c> / <c>u8 QRot</c> / <c>i16 QVx</c> is a valid quantized value by
/// construction — every bit pattern maps to a real coordinate inside the room's bounds. The only inbound
/// floats left in the whole room path are spectator focus coordinates, which Replication validates.
/// </para>
/// </remarks>
public sealed partial class Room
{
    private const long ChatWindowSeconds = 60L;
    private const long ColdPropsWindowSeconds = 1L;

    /// <summary>Routes one decoded frame by TypeId.</summary>
    private void Handle(RoomMember member, in InboundMessage message, uint tick)
    {
        switch (message.TypeId)
        {
            // Hot plane first: this is the frame that arrives per client per tick.
            case MessageTypeIds.EntityUpdatePacket:
                HandleEntityUpdatePacket(member, in message);
                break;
            case MessageTypeIds.SpawnEntityRequest:
                HandleSpawnEntity(member, in message);
                break;
            case MessageTypeIds.DespawnEntityCommand:
                HandleDespawnEntity(member, in message);
                break;
            case MessageTypeIds.SetEntityPropsCommand:
                HandleSetEntityProps(member, in message);
                break;
            case MessageTypeIds.SendChatCommand:
                HandleSendChat(member, in message);
                break;
            case MessageTypeIds.SetRoomVarCommand:
                HandleSetRoomVar(member, in message);
                break;
            case MessageTypeIds.EmitSignalCommand:
                HandleEmitSignal(member, in message, tick);
                break;
            case MessageTypeIds.ResyncCommand:
                // Empty payload: the TypeId is the whole message, so there is nothing to deserialize.
                HandleResync(member);
                break;
            case MessageTypeIds.SetClientPrefsCommand:
                HandleSetClientPrefs(member, in message);
                break;
            case MessageTypeIds.LeaveCommand:
                HandleLeaveCommand(member);
                break;
            default:
                HandleUnroutable(member, message.TypeId);
                break;
        }
    }

    // ── Hot plane: client entity updates ──────────────────────────────────────

    /// <summary>
    /// Applies a client's <c>EntityUpdatePacket</c>. Every record is re-decoded here rather than trusted
    /// from the edge, and ownership, mask legality, generation staleness and the Level-1 speed check all
    /// live in Replication, which owns both the entity table and the per-client violation counters.
    /// </summary>
    /// <remarks>
    /// The client's tick is advisory — the server stamps its own — and the sender's area of interest is
    /// <b>not</b> touched here: focus follows its owned entity's server-side position, refreshed every
    /// tick by Replication, so a client cannot claim a position at all.
    /// </remarks>
    private void HandleEntityUpdatePacket(RoomMember member, in InboundMessage message)
    {
        if (!HotWire.TryReadEntityUpdatePacket(message.Frame, out uint clientTick, out int count, out ReadOnlySpan<byte> records))
        {
            _malformedMessages++;
            return;
        }

        _ = clientTick; // Advisory only: never trusted for ordering that affects other clients.

        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            if (!HotWire.TryReadOwnerUpdateRecord(
                    records.Slice(offset),
                    out uint netId,
                    out byte mask,
                    out EntityWireState state,
                    out int bytesRead))
            {
                // The packet lied about its record count or a record was truncated.
                _malformedMessages++;
                return;
            }

            offset += bytesRead;

            if (!_replication.TryApplyOwnedUpdate(netId, member.ClientId, mask, in state))
            {
                _refusedEntityUpdates++;
            }
        }
    }

    // ── Entity lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates one client-owned entity. The transform arrives quantized, so it is copied through verbatim:
    /// the quantized integers <i>are</i> the replicated values, and a spawn must not be able to introduce
    /// a value the delta plane could not express.
    /// </summary>
    private void HandleSpawnEntity(RoomMember member, in InboundMessage message)
    {
        SpawnEntityRequest? request = Deserialize<SpawnEntityRequest>(in message);
        if (request is null)
        {
            return;
        }

        RejectCode reject = RejectCode.None;
        uint netId = NetId.None;
        bool spawned = false;

        if (!IsKindAllowed(request.Kind))
        {
            // An unknown kind indexes past the build's prefab table and would fault every observer's scene
            // code, so it is refused rather than replicated.
            reject = RejectCode.KindNotAllowed;

            // Counted as a `kind` violation, not a quota one: the allowlist is a correctness rule about
            // what this build can instantiate, not a rate.
            _replication.CountKindViolation(member.ClientId);
        }
        else if (member.OwnedEntityCount >= _options.MaxEntitiesPerOwner)
        {
            reject = RejectCode.QuotaExceeded;
            member.QuotaViolations++;
        }
        else
        {
            EntityWireState state = default;
            state.QX = request.QX;
            state.QY = request.QY;
            state.QRot = request.QRot;
            state.QVx = request.QVx;
            state.QVy = request.QVy;
            state.Kind = request.Kind;
            state.OwnerId = member.ClientId;

            // Flags bit 2 is reserved and must be ignored on receipt; the ownership-policy bits are the
            // client's to choose, and they decide the entity's fate when its owner leaves.
            state.Flags = (byte)(request.Flags & ~EntityFlags.ReservedMask);

            spawned = _replication.TrySpawn(member.ClientId, request.Kind, in state, out netId, out reject);
            if (spawned)
            {
                _entities[netId] = new EntityInfo(member.ClientId, null);
                member.TryAddOwnedEntity(netId);

                // Focus binds to the first live owned entity, in spawn order — so the first entity a client
                // spawns becomes its AOI centre and stays it until it despawns.
                if (member.FocusNetId == NetId.None)
                {
                    RebindFocus(member, netId);
                }
            }
        }

        _spawnResponseScratch.RequestId = request.RequestId;
        _spawnResponseScratch.NetId = spawned ? netId : NetId.None;
        _spawnResponseScratch.RejectCode = (ushort)(spawned ? RejectCode.None : reject);
        SendTo(member.ClientId, MessageTypeIds.SpawnEntityResponse, _spawnResponseScratch);

        if (!spawned)
        {
            _spawnRejections++;
            _logger.LogDebug(
                "Room {RoomId} refused a spawn of kind {Kind} from client {ClientId}: {Reject}",
                _config.RoomId, request.Kind, member.ClientId, reject);
            return;
        }

        byte[]? props = request.Props;
        if (props is { Length: > 0 })
        {
            StoreAndFanOutProps(member.ClientId, netId, props);
        }
    }

    private void HandleDespawnEntity(RoomMember member, in InboundMessage message)
    {
        DespawnEntityCommand? request = Deserialize<DespawnEntityCommand>(in message);
        if (request is null)
        {
            return;
        }

        uint netId = request.NetId;
        if (!_replication.TryDespawn(netId, member.ClientId, out RejectCode reject))
        {
            _logger.LogDebug(
                "Room {RoomId} refused a despawn of {NetId} from client {ClientId}: {Reject}",
                _config.RoomId, netId, member.ClientId, reject);
            return;
        }

        _entities.Remove(netId);

        if (member.RemoveOwnedEntity(netId) && member.FocusNetId == netId)
        {
            // The focus entity is gone: re-bind to the next one this client spawned, or to nothing at all —
            // a client owning nothing is a spectator, and a spectator cannot scope a signal to an AOI.
            RebindFocus(member, member.FirstOwnedEntity);
        }
    }

    /// <summary>
    /// Binds (or unbinds) this member's AOI centre. Called only on spawn/despawn transitions — never per
    /// tick, because Replication re-resolves the bound entity's position itself every tick.
    /// </summary>
    private void RebindFocus(RoomMember member, uint netId)
    {
        member.FocusNetId = netId;
        if (member.SubscriberAdded)
        {
            _replication.BindSubscriberFocus(member.ClientId, netId);
        }
    }

    /// <summary>
    /// True when this room accepts the entity kind. An empty allowlist accepts anything, which the config
    /// validator permits and production configuration is expected to forbid.
    /// </summary>
    private bool IsKindAllowed(ushort kind) => _allowedKinds is null || _allowedKinds.Contains(kind);

    private void HandleSetEntityProps(RoomMember member, in InboundMessage message)
    {
        SetEntityPropsCommand? request = Deserialize<SetEntityPropsCommand>(in message);
        if (request is null)
        {
            return;
        }

        byte[]? json = request.Json;
        if (json is null)
        {
            _coldPropsRejections++;
            member.QuotaViolations++;
            return;
        }

        ref EntityInfo entity = ref CollectionsMarshal.GetValueRefOrNullRef(_entities, request.NetId);
        if (Unsafe.IsNullRef(ref entity) || entity.OwnerId != member.ClientId)
        {
            // Cold props are owner-only, and the room's mirror is what knows who owns what.
            _coldPropsRejections++;
            member.QuotaViolations++;
            return;
        }

        StoreAndFanOutProps(member.ClientId, request.NetId, json);
    }

    /// <summary>
    /// Stores an entity's cold props and tells the other members about them, within the per-entity size and
    /// rate limits (512 B, 2/s by default).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fan-out is room-wide, not AOI-scoped.</b> The protocol wants these delivered to the subscribers
    /// that can see the entity, but <c>IRoomReplication</c> exposes no "who can see this entity" query, so
    /// the event goes to every member of the room (never outside it). Cold props are rate-limited to 2/s
    /// per entity, so the cost is bounded; narrowing it needs a replication-side API and is a deliberate
    /// follow-up. Peers that enter the AOI later are covered the same way: they get the value when it next
    /// changes, and the stored copy is what a future AOI-enter hook will serve.
    /// </para>
    /// <para>
    /// The <c>ColdDirty</c> delta bit is set alongside, which is the wire's promise that this control-plane
    /// event is coming.
    /// </para>
    /// </remarks>
    private void StoreAndFanOutProps(uint ownerId, uint netId, byte[] json)
    {
        if (json.Length > _options.MaxColdPropsBytes)
        {
            _coldPropsRejections++;
            CountQuotaViolation(ownerId);
            _logger.LogDebug(
                "Room {RoomId} refused {ByteCount} bytes of cold props for {NetId} (cap {Cap})",
                _config.RoomId, json.Length, netId, _options.MaxColdPropsBytes);
            return;
        }

        ref EntityInfo entity = ref CollectionsMarshal.GetValueRefOrNullRef(_entities, netId);
        if (Unsafe.IsNullRef(ref entity))
        {
            _coldPropsRejections++;
            CountQuotaViolation(ownerId);
            return;
        }

        if (!TryConsumeColdPropsAllowance(ref entity))
        {
            _coldPropsRejections++;
            CountQuotaViolation(ownerId);
            return;
        }

        entity.ColdProps = json;
        _replication.TryMarkColdDirty(netId);

        _propsScratch.NetId = netId;
        _propsScratch.Json = json;
        BroadcastControlExcept(MessageTypeIds.EntityPropsChangedEvent, _propsScratch, ownerId);
        _propsScratch.Json = []; // Don't pin a client payload inside the reusable scratch message.
    }

    /// <summary>Fixed one-second window per entity; cheaper than a sliding window and good enough.</summary>
    private bool TryConsumeColdPropsAllowance(ref EntityInfo entity)
    {
        long now = Stopwatch.GetTimestamp();
        long window = Stopwatch.Frequency * ColdPropsWindowSeconds;

        if (entity.ColdPropsWindowStart == 0L || now - entity.ColdPropsWindowStart >= window)
        {
            entity.ColdPropsWindowStart = now;
            entity.ColdPropsCountInWindow = 0;
        }

        if (entity.ColdPropsCountInWindow >= _options.MaxColdPropsPerSecond)
        {
            return false;
        }

        entity.ColdPropsCountInWindow++;
        return true;
    }

    // ── Chat ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sanitises, rate-limits and fans out a chat message <b>to this room only</b> — the reference
    /// server's global broadcast is exactly the bug this fabric exists to avoid.
    /// </summary>
    private void HandleSendChat(RoomMember member, in InboundMessage message)
    {
        SendChatCommand? request = Deserialize<SendChatCommand>(in message);
        if (request is null)
        {
            return;
        }

        if (!TryConsumeChatAllowance(member))
        {
            _chatThrottled++;
            member.QuotaViolations++;
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
        SetRoomVarCommand? request = Deserialize<SetRoomVarCommand>(in message);
        if (request is null)
        {
            return;
        }

        if (_options.RestrictRoomVarsToHost && member.ClientId != HostClientId)
        {
            // No wire code exists for "not the host" that isn't also a close reason, so the write is
            // dropped and counted rather than tearing the session down.
            _roomVarRejections++;
            member.QuotaViolations++;
            return;
        }

        string key = RoomText.Sanitize(request.Key, _options.MaxRoomVarKeyLength);
        if (key.Length == 0)
        {
            _roomVarRejections++;
            member.QuotaViolations++;
            return;
        }

        byte[]? value = request.Value;
        if (value is null || value.Length > _options.MaxRoomVarValueBytes)
        {
            _roomVarRejections++;
            member.QuotaViolations++;
            return;
        }

        if (!_roomVars.ContainsKey(key) && _roomVars.Count >= _options.MaxRoomVars)
        {
            _roomVarRejections++;
            member.QuotaViolations++;
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
        BroadcastControl(MessageTypeIds.RoomVarsChangedEvent, _roomVarsScratch);
        _roomVarValueScratch[0] = [];
    }

    /// <summary>
    /// Sends the complete room-var set to a joiner — and to a resumed session, whose copy may have gone
    /// stale while it was away.
    /// </summary>
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
        var full = new RoomVarsChangedEvent { Keys = keys, Values = values };
        SendTo(member.ClientId, MessageTypeIds.RoomVarsChangedEvent, full);
    }

    // ── Roster ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the complete room roster to a joiner — and to a resumed session, whose copy may have gone
    /// stale while it was away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The roster <b>includes the recipient itself</b> and is room-scoped, never AOI-scoped, exactly
    /// like the <c>PeerJoinedEvent</c>/<c>PeerLeftEvent</c> pair it completes. A resume gets the full
    /// set for the same reason it gets the full room-var set: members may have joined or left during
    /// the grace, and a resumed client must not reset its local state to find out.
    /// </para>
    /// <para>
    /// <b>Chunked against the frame cap, computed rather than guessed.</b> A display name is up to 32
    /// <i>characters</i>, which is up to 128 UTF-8 <i>bytes</i>, so a fixed entry count would either
    /// waste most of a frame or overflow it. Each chunk is filled while its exact encoded size still
    /// fits, and only the last one carries <c>FrameFlags.Final</c>. One chunk always goes out, even
    /// for an impossible empty roster, so the client is never left waiting for a completion.
    /// </para>
    /// </remarks>
    private void SendRoomRoster(RoomMember recipient)
    {
        // Membership may have moved since the tick's first refresh (a leave processed earlier in this
        // very tick), and the roster must be the set as it is now.
        RefreshMemberList();

        int budget = Math.Min(_options.MaxFrameBytes, RoomRosterEvent.MaxPayloadBytes);
        int start = 0;

        while (true)
        {
            int displayNamesSize = RoomRosterEvent.EmptyDisplayNamesSize;
            int end = start;

            // At least one member per chunk regardless of the budget: a chunk that fits nothing would
            // loop forever, and a single entry can never approach 4 KiB.
            while (end < _memberCount)
            {
                int nameSize = RoomRosterEvent.EncodedDisplayNameSize(_memberList[end].Connection.DisplayName);
                if (end > start
                    && RoomRosterEvent.EncodedFrameSize(end - start + 1, displayNamesSize + nameSize) > budget)
                {
                    break;
                }

                displayNamesSize += nameSize;
                end++;
            }

            bool final = end >= _memberCount;
            SendRosterChunk(recipient, start, end, final);

            if (final)
            {
                return;
            }

            start = end;
        }
    }

    /// <summary>Sends members <c>[start, end)</c> as one self-contained roster chunk.</summary>
    private void SendRosterChunk(RoomMember recipient, int start, int end, bool final)
    {
        int count = end - start;
        var clientIds = new uint[count];
        var displayNames = new string[count];

        for (int i = 0; i < count; i++)
        {
            RoomMember member = _memberList[start + i];
            clientIds[i] = member.ClientId;
            displayNames[i] = member.Connection.DisplayName;
        }

        // A join is rare enough to deserve its own event instance rather than the shared scratch, and
        // the arrays are chunk-sized anyway — the same trade SendFullRoomVars makes.
        var chunk = new RoomRosterEvent
        {
            ClientIds = clientIds,
            DisplayNames = displayNames,
            FrameFlags = final ? Protocol.FrameFlags.Final : Protocol.FrameFlags.None,
        };

        SendTo(recipient.ClientId, MessageTypeIds.RoomRosterEvent, chunk);
    }

    // ── Signals ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes one signal by target: AOI-scoped signals join this tick's hot batch, all-peers and
    /// single-peer signals go out as <c>SignalEvent</c> on the control lane, and a server-targeted signal
    /// is the room's own business — of which a Relay room has none.
    /// </summary>
    /// <remarks>
    /// There is deliberately <b>no</b> "AOI falls back to everyone" path. That fallback turns one client's
    /// emit into one frame per member: a 600× amplifier, and precisely the reason the batch exists.
    /// </remarks>
    private void HandleEmitSignal(RoomMember member, in InboundMessage message, uint tick)
    {
        EmitSignalCommand? request = Deserialize<EmitSignalCommand>(in message);
        if (request is null)
        {
            return;
        }

        string name = RoomText.Sanitize(request.Name, _options.MaxSignalNameLength);
        if (name.Length == 0)
        {
            RefuseSignal(member, "empty name", tick);
            return;
        }

        byte[] payload = request.Payload ?? [];
        if (payload.Length > _options.MaxSignalPayloadBytes)
        {
            RefuseSignal(member, "payload too large", tick);
            return;
        }

        switch ((SignalTarget)request.Target)
        {
            case SignalTarget.Server:
                // A Relay room holds no server-side game logic, so there is nobody to deliver this to and
                // nothing is fanned out. Counted rather than ignored: it usually means a mis-targeted call.
                _serverTargetedSignals++;
                return;

            case SignalTarget.AoiPeers:
                QueueAoiSignal(member, name, payload, tick);
                return;

            case SignalTarget.AllPeers:
                _signalScratch.SenderClientId = member.ClientId;
                _signalScratch.Name = name;
                _signalScratch.Payload = payload;
                BroadcastControlExcept(MessageTypeIds.SignalEvent, _signalScratch, member.ClientId);
                _signalScratch.Payload = [];
                return;

            case SignalTarget.SinglePeer:
                _signalScratch.SenderClientId = member.ClientId;
                _signalScratch.Name = name;
                _signalScratch.Payload = payload;
                SendTo(request.TargetClientId, MessageTypeIds.SignalEvent, _signalScratch);
                _signalScratch.Payload = [];
                return;

            default:
                RefuseSignal(member, "unknown target", tick);
                return;
        }
    }

    /// <summary>
    /// Queues an AOI-scoped signal for this tick's batch. The name is measured in <b>UTF-8 bytes</b>,
    /// because that is what a <c>SignalBatchPacket</c> entry carries (1…64 bytes, payload 0…255): a 64-
    /// character name can be 256 bytes and is simply not eligible for the hot plane.
    /// </summary>
    private void QueueAoiSignal(RoomMember member, string name, byte[] payload, uint tick)
    {
        int nameBytes = Encoding.UTF8.GetBytes(name, _signalNameScratch);
        if (nameBytes < HotWire.MinSignalNameLength
            || nameBytes > HotWire.MaxSignalNameLength
            || payload.Length > HotWire.MaxSignalPayloadLength)
        {
            RefuseSignal(member, "not eligible for the hot plane", tick);
            return;
        }

        // Replication refuses a sender with no bound focus entity — a spectator cannot scope a signal to an
        // area of interest at all — and a batch that is full or an entry that does not fit.
        if (!_replication.TryQueueAoiSignal(member.ClientId, _signalNameScratch.AsSpan(0, nameBytes), payload))
        {
            RefuseSignal(member, "no focus entity or batch full", tick);
        }
    }

    private void RefuseSignal(RoomMember member, string why, uint tick)
    {
        _signalRejections++;
        member.QuotaViolations++;
        _logger.LogDebug(
            "Room {RoomId} refused a signal from client {ClientId} on tick {Tick}: {Reason}",
            _config.RoomId, member.ClientId, tick, why);
    }

    // ── Session ───────────────────────────────────────────────────────────────

    /// <summary>
    /// "My known set is untrustworthy, re-send it": clears this client's known set and restarts its
    /// snapshot. <c>Net</c> already rationed the request to 2/s.
    /// </summary>
    private void HandleResync(RoomMember member)
    {
        _replication.RequestResync(member.ClientId);
        _resyncs++;
    }

    /// <summary>
    /// Applies hidden-tab and send-rate preferences. Un-hiding implies a resync, which the replication core
    /// does itself — a client that received nothing for a minute has a known set that is pure fiction.
    /// </summary>
    private void HandleSetClientPrefs(RoomMember member, in InboundMessage message)
    {
        SetClientPrefsCommand? request = Deserialize<SetClientPrefsCommand>(in message);
        if (request is null)
        {
            return;
        }

        // 0 and 1 both mean "every tick" on the wire; anything above 8 is clamped rather than refused,
        // because a divisor is a hint about the client's own capacity, not an assertion about the room.
        byte divisor = request.SendRateDivisor <= 1 ? (byte)1 : Math.Min(request.SendRateDivisor, (byte)8);
        _replication.SetSubscriberSendDivisor(member.ClientId, divisor);
        _replication.SetSubscriberHidden(member.ClientId, request.Hidden);
    }

    /// <summary>
    /// A voluntary goodbye. <c>Net</c> normally answers this on the socket thread and closes the
    /// connection, so the room only sees it if a future transport forwards it instead — and a voluntary
    /// leave never gets a resume grace.
    /// </summary>
    private void HandleLeaveCommand(RoomMember member)
    {
        Leave(member.ClientId, LeaveReason.LeftVoluntarily);
        member.Connection.RequestClose(RejectCode.None, "left the room");
    }

    private void HandleUnroutable(RoomMember member, byte typeId)
    {
        // Unknown (and app-range) TypeIds are ignored and counted, never fatal: that is what lets a game
        // published six months ago keep working when the fabric adds messages.
        _unroutableMessages++;
        _logger.LogDebug(
            "Room {RoomId} ignored {MessageName} ({TypeId}) from client {ClientId}: the room does not route it",
            _config.RoomId, MessageTypeIds.GetName(typeId), typeId, member.ClientId);
    }

    /// <summary>Attributes a room-level refusal to a member that may or may not still be here.</summary>
    private void CountQuotaViolation(uint clientId)
    {
        if (_members.TryGetValue(clientId, out RoomMember? member))
        {
            member.QuotaViolations++;
        }
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
