import Foundation

/// Reads host settings injected at build time (see `project.yml` → Info.plist).
struct HostConfiguration {
    let webAppUrl: URL
    let apiBaseUrl: String?

    static let fallbackWebAppUrl = URL(string: "http://localhost:5173")!

    static func fromBundle(_ bundle: Bundle = .main) -> HostConfiguration {
        let webAppValue = bundle.object(forInfoDictionaryKey: "StoryTimeWebAppUrl") as? String
        let apiValue = (bundle.object(forInfoDictionaryKey: "StoryTimeApiBaseUrl") as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return HostConfiguration(
            webAppUrl: webAppValue.flatMap(URL.init(string:)) ?? fallbackWebAppUrl,
            apiBaseUrl: apiValue?.isEmpty == false ? apiValue : nil
        )
    }
}
