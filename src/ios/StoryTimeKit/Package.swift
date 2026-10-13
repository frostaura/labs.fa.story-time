// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "StoryTimeKit",
    platforms: [
        .iOS("27.0"),
        .macOS("26.0"),
    ],
    products: [
        .library(name: "StoryTimeKit", targets: ["StoryTimeKit"]),
    ],
    targets: [
        .target(
            name: "StoryTimeKit",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "StoryTimeKitTests",
            dependencies: ["StoryTimeKit"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
