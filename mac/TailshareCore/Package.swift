// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "TailshareCore",
    platforms: [.macOS(.v14)],
    products: [.library(name: "TailshareCore", targets: ["TailshareCore"])],
    dependencies: [
        .package(url: "https://github.com/hummingbird-project/hummingbird.git", exact: "2.17.0"),
        .package(url: "https://github.com/weichsel/ZIPFoundation.git", exact: "0.9.20"),
    ],
    targets: [
        .target(
            name: "TailshareCore",
            dependencies: [
                .product(name: "Hummingbird", package: "hummingbird"),
                .product(name: "ZIPFoundation", package: "ZIPFoundation"),
            ]
        ),
        .testTarget(name: "TailshareCoreTests", dependencies: ["TailshareCore"]),
    ]
)
