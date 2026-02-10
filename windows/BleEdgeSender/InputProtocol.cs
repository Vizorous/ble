using System.Buffers.Binary;

namespace BleEdgeSender;

internal enum PacketType : byte
{
    MouseMove = 0x01,
    MouseButton = 0x02,
    Key = 0x03,
    Wheel = 0x04,
}

internal enum MouseButtonId : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
}

internal static class InputProtocol
{
    public static byte[] MouseMove(short dx, short dy)
    {
        var payload = new byte[5];
        payload[0] = (byte)PacketType.MouseMove;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1, 2), dx);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(3, 2), dy);
        return payload;
    }

    public static byte[] MouseButton(MouseButtonId button, bool isDown)
    {
        return [(byte)PacketType.MouseButton, (byte)button, isDown ? (byte)1 : (byte)0];
    }

    public static byte[] Key(ushort usage, bool isDown)
    {
        var payload = new byte[4];
        payload[0] = (byte)PacketType.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), usage);
        payload[3] = isDown ? (byte)1 : (byte)0;
        return payload;
    }

    public static byte[] Wheel(short delta)
    {
        var payload = new byte[3];
        payload[0] = (byte)PacketType.Wheel;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1, 2), delta);
        return payload;
    }
}
