// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "NagsterCore",
    platforms: [.iOS(.v26), .macOS(.v26)],
    products: [
        .library(name: "NagsterCore", targets: ["NagsterCore"])
    ],
    targets: [
        .target(name: "NagsterCore"),
        .testTarget(name: "NagsterCoreTests", dependencies: ["NagsterCore"])
    ]
)
