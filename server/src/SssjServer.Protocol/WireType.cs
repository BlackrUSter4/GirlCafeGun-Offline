namespace SssjServer.Protocol;

/// <summary>
/// protobuf2 wire types。数值与 protobuf 规范一致。
/// （占位实现：本类由 task A / 其它 agent 真正实现。）
/// </summary>
public enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    Length = 2,
    Fixed32 = 5,
}
