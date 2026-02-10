import AppKit
import ApplicationServices
import CoreBluetooth
import Foundation

private let serviceUUID = CBUUID(string: "8F98B0C3-10C2-4F8F-8E8D-3F567C9D2F01")
private let inputCharUUID = CBUUID(string: "8F98B0C3-10C2-4F8F-8E8D-3F567C9D2F02")

enum PacketType: UInt8 {
    case mouseMove = 0x01
    case mouseButton = 0x02
    case key = 0x03
    case wheel = 0x04
}

enum MouseButtonId: UInt8 {
    case left = 1
    case right = 2
    case middle = 3
}

final class EventInjector {
    private var cursor: CGPoint

    private let usageToKeyCode: [UInt16: CGKeyCode] = [
        0x04: 0, 0x05: 11, 0x06: 8, 0x07: 2, 0x08: 14, 0x09: 3, 0x0A: 5, 0x0B: 4,
        0x0C: 34, 0x0D: 38, 0x0E: 40, 0x0F: 37, 0x10: 46, 0x11: 45, 0x12: 31,
        0x13: 35, 0x14: 12, 0x15: 15, 0x16: 1, 0x17: 17, 0x18: 32, 0x19: 9,
        0x1A: 13, 0x1B: 7, 0x1C: 16, 0x1D: 6,

        0x1E: 18, 0x1F: 19, 0x20: 20, 0x21: 21, 0x22: 23,
        0x23: 22, 0x24: 26, 0x25: 28, 0x26: 25, 0x27: 29,

        0x28: 36,
        0x29: 53,
        0x2A: 51,
        0x2B: 48,
        0x2C: 49,

        0x39: 57,

        0x4F: 124,
        0x50: 123,
        0x51: 125,
        0x52: 126,

        0xE0: 59,
        0xE1: 56,
        0xE2: 58,
        0xE3: 55,
        0xE4: 62,
        0xE5: 60,
        0xE6: 61,
        0xE7: 54,
    ]

    init() {
        cursor = CGEvent(source: nil)?.location ?? .zero
    }

    func moveMouse(dx: Int16, dy: Int16) {
        let bounds = CGDisplayBounds(CGMainDisplayID())
        cursor.x = min(max(bounds.minX, cursor.x + CGFloat(dx)), bounds.maxX)
        cursor.y = min(max(bounds.minY, cursor.y + CGFloat(dy)), bounds.maxY)

        let event = CGEvent(
            mouseEventSource: nil,
            mouseType: .mouseMoved,
            mouseCursorPosition: cursor,
            mouseButton: .left
        )
        event?.post(tap: .cghidEventTap)
    }

    func mouseButton(_ button: MouseButtonId, isDown: Bool) {
        let type: CGEventType
        let cgButton: CGMouseButton

        switch button {
        case .left:
            type = isDown ? .leftMouseDown : .leftMouseUp
            cgButton = .left
        case .right:
            type = isDown ? .rightMouseDown : .rightMouseUp
            cgButton = .right
        case .middle:
            type = isDown ? .otherMouseDown : .otherMouseUp
            cgButton = .center
        }

        let event = CGEvent(
            mouseEventSource: nil,
            mouseType: type,
            mouseCursorPosition: cursor,
            mouseButton: cgButton
        )
        event?.post(tap: .cghidEventTap)
    }

    func key(usage: UInt16, isDown: Bool) {
        guard let keyCode = usageToKeyCode[usage] else {
            return
        }

        let event = CGEvent(keyboardEventSource: nil, virtualKey: keyCode, keyDown: isDown)
        event?.post(tap: .cghidEventTap)
    }

    func wheel(delta: Int16) {
        let lines = Int32(delta / 120)
        if lines == 0 {
            return
        }

        let event = CGEvent(
            scrollWheelEvent2Source: nil,
            units: .line,
            wheelCount: 1,
            wheel1: lines,
            wheel2: 0,
            wheel3: 0
        )
        event?.post(tap: .cghidEventTap)
    }
}

final class BleReceiver: NSObject {
    private var central: CBCentralManager!
    private var peripheral: CBPeripheral?
    private var inputCharacteristic: CBCharacteristic?
    private let injector = EventInjector()
    private var packetCount = 0

    override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: nil)
    }

    private func parse(_ data: Data) {
        guard let type = data.first.flatMap(PacketType.init(rawValue:)) else {
            return
        }
        packetCount += 1

        switch type {
        case .mouseMove:
            guard data.count >= 5 else { return }
            let dx = Self.readInt16LE(data, at: 1)
            let dy = Self.readInt16LE(data, at: 3)
            if packetCount % 200 == 0 {
                print("rx mouseMove dx=\(dx) dy=\(dy)")
            }
            injector.moveMouse(dx: dx, dy: dy)

        case .mouseButton:
            guard data.count >= 3,
                  let button = MouseButtonId(rawValue: data[1])
            else { return }
            print("rx mouseButton button=\(button.rawValue) down=\(data[2] != 0)")
            injector.mouseButton(button, isDown: data[2] != 0)

        case .key:
            guard data.count >= 4 else { return }
            let usage = Self.readUInt16LE(data, at: 1)
            print("rx key usage=0x\(String(usage, radix: 16)) down=\(data[3] != 0)")
            injector.key(usage: usage, isDown: data[3] != 0)

        case .wheel:
            guard data.count >= 3 else { return }
            let delta = Self.readInt16LE(data, at: 1)
            print("rx wheel delta=\(delta)")
            injector.wheel(delta: delta)
        }
    }

    private static func readUInt16LE(_ data: Data, at index: Int) -> UInt16 {
        UInt16(data[index]) | (UInt16(data[index + 1]) << 8)
    }

    private static func readInt16LE(_ data: Data, at index: Int) -> Int16 {
        Int16(bitPattern: readUInt16LE(data, at: index))
    }
}

extension BleReceiver: CBCentralManagerDelegate {
    private func stateName(_ state: CBManagerState) -> String {
        switch state {
        case .unknown: return "unknown"
        case .resetting: return "resetting"
        case .unsupported: return "unsupported"
        case .unauthorized: return "unauthorized"
        case .poweredOff: return "poweredOff"
        case .poweredOn: return "poweredOn"
        @unknown default: return "other"
        }
    }

    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        print("Central state: \(stateName(central.state))")
        switch central.state {
        case .poweredOn:
            print("Bluetooth ON, scanning for sender...")
            central.scanForPeripherals(withServices: nil, options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
        default:
            break
        }
    }

    func centralManager(_ central: CBCentralManager,
                        didDiscover peripheral: CBPeripheral,
                        advertisementData: [String: Any],
                        rssi RSSI: NSNumber)
    {
        if self.peripheral != nil {
            return
        }

        let name = peripheral.name ?? "unknown"
        let advertisedServiceUUIDs = (advertisementData[CBAdvertisementDataServiceUUIDsKey] as? [CBUUID]) ?? []
        let advertisedServiceText = advertisedServiceUUIDs.map(\.uuidString).joined(separator: ",")
        print("Discovered peripheral: name=\(name) id=\(peripheral.identifier.uuidString) rssi=\(RSSI) services=[\(advertisedServiceText)]")

        let hasTargetService = advertisedServiceUUIDs.contains(serviceUUID)
        if !hasTargetService {
            return
        }

        self.peripheral = peripheral
        peripheral.delegate = self
        print("Found sender with target service: \(name), connecting...")
        central.stopScan()
        central.connect(peripheral, options: nil)
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        print("Connected to sender")
        peripheral.discoverServices([serviceUUID])
    }

    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        print("Failed to connect: \(error?.localizedDescription ?? "unknown error"), rescanning...")
        self.peripheral = nil
        self.inputCharacteristic = nil
        central.scanForPeripherals(withServices: nil, options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        print("Disconnected from sender (\(error?.localizedDescription ?? "no error")), rescanning...")
        self.peripheral = nil
        self.inputCharacteristic = nil
        central.scanForPeripherals(withServices: nil, options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
    }
}

extension BleReceiver: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        if let error {
            print("discoverServices error: \(error)")
            return
        }

        guard let services = peripheral.services else {
            print("No services found on peripheral")
            return
        }
        print("Discovered \(services.count) service(s)")
        for service in services where service.uuid == serviceUUID {
            peripheral.discoverCharacteristics([inputCharUUID], for: service)
        }
    }

    func peripheral(_ peripheral: CBPeripheral,
                    didDiscoverCharacteristicsFor service: CBService,
                    error: Error?)
    {
        if let error {
            print("discoverCharacteristics error: \(error)")
            return
        }

        guard let characteristics = service.characteristics else {
            print("No characteristics found for service \(service.uuid.uuidString)")
            return
        }
        print("Discovered \(characteristics.count) characteristic(s) for service \(service.uuid.uuidString)")
        for characteristic in characteristics where characteristic.uuid == inputCharUUID {
            inputCharacteristic = characteristic
            peripheral.setNotifyValue(true, for: characteristic)
            print("Subscribed to input characteristic")
        }
    }

    func peripheral(_ peripheral: CBPeripheral,
                    didUpdateNotificationStateFor characteristic: CBCharacteristic,
                    error: Error?)
    {
        if let error {
            print("Notification state update failed: \(error.localizedDescription)")
            return
        }
        print("Notification state for \(characteristic.uuid.uuidString): \(characteristic.isNotifying)")
    }

    func peripheral(_ peripheral: CBPeripheral,
                    didUpdateValueFor characteristic: CBCharacteristic,
                    error: Error?)
    {
        if let error {
            print("didUpdateValue error: \(error)")
            return
        }

        guard characteristic.uuid == inputCharUUID,
              let data = characteristic.value
        else { return }

        parse(data)
    }
}

let options = [kAXTrustedCheckOptionPrompt.takeRetainedValue() as String: true] as CFDictionary
_ = AXIsProcessTrustedWithOptions(options)

print("BleEdgeReceiver starting...")
print("Grant Accessibility access when prompted (System Settings > Privacy & Security > Accessibility).")
let receiver = BleReceiver()
_ = receiver
RunLoop.main.run()
