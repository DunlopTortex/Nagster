import Foundation
import NagsterCore

enum SyncError: LocalizedError, Equatable {
    case notSetUp
    case signInNeeded
    case wrongPassword(String)
    case unreachable(String)
    case server(String)

    var errorDescription: String? {
        switch self {
        case .notSetUp: "Enter your PC's address in Settings."
        case .signInNeeded: "Sign in to your PC again in Settings."
        case .wrongPassword(let message), .server(let message): message
        case .unreachable(let detail): "Couldn't reach your PC (\(detail)). Check it's on and on the same network or Tailscale."
        }
    }
}

/// Talks to Nagster on the PC through its web interface: signs in with the web password, then syncs.
/// The PC requires the session cookie on every call and the X-DGNA header on every change (its CSRF guard).
struct SyncClient {
    static let sessionCookie = "dgna_session"
    private static let sessionAccount = "pc-session"

    var baseURL: URL
    var session: URLSession = .shared

    /// Reads an address as typed: "192.168.1.20", "192.168.1.20:5972" or "http://my-pc:5972".
    static func baseURL(from text: String) -> URL? {
        var trimmed = text.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return nil }
        if !trimmed.contains("://") { trimmed = "http://" + trimmed }
        guard var components = URLComponents(string: trimmed), components.host?.isEmpty == false else { return nil }
        if components.port == nil { components.port = 5972 }
        components.path = ""
        return components.url
    }

    static var isSignedIn: Bool { Keychain.read(sessionAccount) != nil }

    static func signOut() { Keychain.delete(sessionAccount) }

    func signIn(password: String) async throws {
        struct Login: Encodable { var password: String }
        let (data, response) = try await send("api/login", body: Login(password: password), signedIn: false)
        guard response.statusCode == 204 else { throw SyncError.wrongPassword(Self.message(in: data) ?? "That password isn't right.") }
        try storeSession(from: response)
    }

    func sync(_ request: SyncRequest) async throws -> SyncResponse {
        let (data, response) = try await send("api/sync", body: request, signedIn: true)
        switch response.statusCode {
        case 200:
            try? storeSession(from: response) // The PC renews the session as it's used.
            do { return try JSONDecoder().decode(SyncResponse.self, from: data) } catch {
                throw SyncError.server("The PC's reply couldn't be read; update Nagster on the PC or the phone.")
            }
        case 401:
            Self.signOut()
            throw SyncError.signInNeeded
        default:
            throw SyncError.server(Self.message(in: data) ?? "The PC replied with an error (\(response.statusCode)).")
        }
    }

    private func send(_ path: String, body: some Encodable, signedIn: Bool) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: baseURL.appending(path: path), timeoutInterval: 20)
        request.httpMethod = "POST"
        request.httpShouldHandleCookies = false
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("1", forHTTPHeaderField: "X-DGNA")
        if signedIn {
            guard let token = Keychain.read(Self.sessionAccount) else { throw SyncError.signInNeeded }
            request.setValue("\(Self.sessionCookie)=\(token)", forHTTPHeaderField: "Cookie")
        }
        request.httpBody = try JSONEncoder().encode(body)
        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw SyncError.unreachable("no reply") }
            return (data, http)
        } catch let error as URLError {
            throw SyncError.unreachable(error.localizedDescription)
        }
    }

    private func storeSession(from response: HTTPURLResponse) throws {
        let headers = response.allHeaderFields.reduce(into: [String: String]()) { result, pair in
            if let key = pair.key as? String, let value = pair.value as? String { result[key] = value }
        }
        let cookies = HTTPCookie.cookies(withResponseHeaderFields: headers, for: baseURL)
        guard let token = cookies.first(where: { $0.name == Self.sessionCookie })?.value else {
            throw SyncError.server("The PC didn't start a session.")
        }
        Keychain.write(token, for: Self.sessionAccount)
    }

    private static func message(in data: Data) -> String? {
        struct Problem: Decodable { var error: String }
        return try? JSONDecoder().decode(Problem.self, from: data).error
    }
}
