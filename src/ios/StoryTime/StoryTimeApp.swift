import SwiftUI

@main
struct StoryTimeApp: App {
    var body: some Scene {
        WindowGroup {
            WebShellView(configuration: .fromBundle())
                .ignoresSafeArea(edges: .bottom)
        }
    }
}
