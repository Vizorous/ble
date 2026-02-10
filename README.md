# BLE Edge Input (Windows -> macOS)

This project provides a prototype for sending mouse/keyboard input from a Windows PC to a Mac over BLE.

- Windows app (`windows/BleEdgeSender`): BLE GATT peripheral + global input capture.
- macOS app (`mac/BleEdgeReceiver`): BLE central + input injection.

When your cursor reaches the left edge on Windows, remote mode is enabled and input is sent to macOS.

## Why this approach

Windows BLE HID peripheral support is not practical for a normal desktop app, so this uses a custom BLE GATT protocol.

## Current behavior

- Enter remote mode: move cursor to the left edge on Windows.
- Exit remote mode: press `F12`.
- If Mac disconnects, remote mode is disabled automatically.

## 1) Build and run on Windows (PowerShell)

```powershell
cd windows/BleEdgeSender
dotnet restore
dotnet run
```

Notes:
- Run with Administrator privileges if global hooks fail.
- Keep Bluetooth enabled.
- Pairing is handled by OS; this app advertises a custom BLE service.

## 2) Build and run on macOS

```bash
cd mac/BleEdgeReceiver
swift build
swift run BleEdgeReceiver
```

On first run, grant Accessibility permission:
- System Settings -> Privacy & Security -> Accessibility
- Enable the terminal/app running `BleEdgeReceiver`.

## 3) Use it

1. Start the macOS receiver first.
2. Start the Windows sender.
3. Wait for "Mac receiver subscribed." on Windows.
4. Move mouse to the left edge of Windows screen to hand off control.
5. Press `F12` on Windows keyboard to return control locally.

## Known limitations

- This is a prototype and not encrypted at the application layer.
- Keyboard mapping is partial (common keys are mapped).
- Mouse delta extraction relies on low-level mouse hook coordinates.
- No multi-monitor topology sync yet.

## Files

- `windows/BleEdgeSender/Program.cs`
- `windows/BleEdgeSender/BlePeripheralServer.cs`
- `windows/BleEdgeSender/InputCapture.cs`
- `windows/BleEdgeSender/InputProtocol.cs`
- `windows/BleEdgeSender/HidUsageMap.cs`
- `mac/BleEdgeReceiver/Sources/BleEdgeReceiver/main.swift`
- `docs/protocol.md`
