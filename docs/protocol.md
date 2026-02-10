# BLE Edge Input Protocol

- Service UUID: `8f98b0c3-10c2-4f8f-8e8d-3f567c9d2f01`
- Characteristic UUID (Notify): `8f98b0c3-10c2-4f8f-8e8d-3f567c9d2f02`

All values are little-endian.

## Packets

- `0x01 MouseMove`: `[type:1][dx:int16][dy:int16]`
- `0x02 MouseButton`: `[type:1][button:uint8][isDown:uint8]`
- `0x03 Key`: `[type:1][usage:uint16][isDown:uint8]`
- `0x04 Wheel`: `[type:1][delta:int16]`

## MouseButton values

- `1` left
- `2` right
- `3` middle
