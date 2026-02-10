// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "BleEdgeReceiver",
    platforms: [
        .macOS(.v13),
    ],
    products: [
        .executable(name: "BleEdgeReceiver", targets: ["BleEdgeReceiver"]),
    ],
    targets: [
        .executableTarget(
            name: "BleEdgeReceiver",
            path: "Sources/BleEdgeReceiver"
        ),
    ]
)
