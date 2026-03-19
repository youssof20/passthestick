using System.Text.Json.Serialization;

namespace PassTheStick.Shared;

// Wire format: JSON with "type" field. Relay is language-agnostic.

public record JoinMessage(
    [property: JsonPropertyName("type")] string Type = "JOIN",
    [property: JsonPropertyName("roomCode")] string RoomCode = "",
    [property: JsonPropertyName("name")] string Name = "");

public record CreateMessage([property: JsonPropertyName("type")] string Type = "CREATE");

public record CreatedMessage(
    [property: JsonPropertyName("type")] string Type = "CREATED",
    [property: JsonPropertyName("roomCode")] string RoomCode = "");

public record PlayerListMessage(
    [property: JsonPropertyName("type")] string Type = "PLAYER_LIST",
    [property: JsonPropertyName("players")] List<PlayerInfo>? Players = null);

public record PlayerInfo(
    [property: JsonPropertyName("id")] string Id = "",
    [property: JsonPropertyName("name")] string Name = "");

public record PassStickMessage(
    [property: JsonPropertyName("type")] string Type = "PASS_STICK",
    [property: JsonPropertyName("toId")] string ToId = "");

public record YouHaveItMessage([property: JsonPropertyName("type")] string Type = "YOU_HAVE_IT");

public record KeyEventMessage(
    [property: JsonPropertyName("type")] string Type = "KEY_EVENT",
    [property: JsonPropertyName("vk")] int Vk = 0,
    [property: JsonPropertyName("sc")] int Sc = 0,
    [property: JsonPropertyName("down")] bool Down = false,
    [property: JsonPropertyName("fromId")] string FromId = "");

public record PadStateMessage(
    [property: JsonPropertyName("type")] string Type = "PAD_STATE",
    [property: JsonPropertyName("btns")] int Btns = 0,
    [property: JsonPropertyName("lx")] short Lx = 0,
    [property: JsonPropertyName("ly")] short Ly = 0,
    [property: JsonPropertyName("rx")] short Rx = 0,
    [property: JsonPropertyName("ry")] short Ry = 0,
    [property: JsonPropertyName("lt")] byte Lt = 0,
    [property: JsonPropertyName("rt")] byte Rt = 0,
    [property: JsonPropertyName("fromId")] string FromId = "");

public record HostRejoinMessage(
    [property: JsonPropertyName("type")] string Type = "HOST_REJOIN",
    [property: JsonPropertyName("roomCode")] string RoomCode = "");

public record PingMessage(
    [property: JsonPropertyName("type")] string Type = "PING",
    [property: JsonPropertyName("ts")] long Ts = 0);

public record PongMessage(
    [property: JsonPropertyName("type")] string Type = "PONG",
    [property: JsonPropertyName("ts")] long Ts = 0);

public record SessionEndedMessage(
    [property: JsonPropertyName("type")] string Type = "SESSION_ENDED",
    [property: JsonPropertyName("reason")] string Reason = "");

public record CloseRoomMessage(
    [property: JsonPropertyName("type")] string Type = "CLOSE_ROOM",
    [property: JsonPropertyName("reason")] string Reason = "Host ended the session");

public record ErrorMessage(
    [property: JsonPropertyName("type")] string Type = "ERROR",
    [property: JsonPropertyName("msg")] string Msg = "");
