#nullable enable
using System;
using RemotePlayServer.Core;

namespace RemotePlayServer.Services;

public enum RoomState { Idle, Configuring, Streaming }

public class RoomService
{
    public string? RoomId { get; private set; }
    public string? DisplayRoomId => RoomId != null && RoomId.Length == 6
        ? $"{RoomId[..3]}-{RoomId[3..]}" : RoomId;
    public string? Password { get; set; }
    public RoomState State { get; private set; } = RoomState.Idle;

    public event Action<RoomState>? OnStateChanged;

    public void SetRoomId(string roomId)
    {
        RoomId = roomId;
        Logger.Info($"[Room] Room ID set: {DisplayRoomId}");
    }

    public void SetState(RoomState state)
    {
        State = state;
        OnStateChanged?.Invoke(state);
        Logger.Info($"[Room] State → {state}");
    }

    public void Reset()
    {
        RoomId = null;
        Password = null;
        State = RoomState.Idle;
    }
}
