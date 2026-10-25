import Foundation
import NagsterCore

enum CloudError: LocalizedError, Equatable {
    case signInNeeded
    case unreachable(String)
    case server(String)
    case notSetUp
    /// Another device holds the applier role now; this one's changes weren't published.
    case notApplier

    var errorDescription: String? {
        switch self {
        case .signInNeeded: "Cloud sync's sign-in has ended (it expired, or the account was deleted): turn it on again in Settings."
        case .unreachable(let detail): "Couldn't reach Supabase (\(detail))."
        case .server(let message): message
        case .notSetUp: "The Nagster tables aren't in that Supabase project yet: run shared/cloud/schema.sql in its SQL editor."
        case .notApplier: "Another device is applying changes right now."
        }
    }
}

/// A signed-in Supabase session. Supabase rotates the refresh token on every use, so keep the newest.
struct CloudSession: Sendable {
    var accessToken: String
    var refreshToken: String
    var expiresAt: Date
    var userId: String
}

/// The account's vault: the wrapped data key and how to derive the key that unwraps it.
struct CloudVault: Codable, Sendable {
    var vaultId: String
    var kdfSalt: String
    var kdfIterations: Int
    var wrappedKey: String

    enum CodingKeys: String, CodingKey {
        case vaultId = "vault_id", kdfSalt = "kdf_salt", kdfIterations = "kdf_iterations", wrappedKey = "wrapped_key"
    }
}

/// An action in the cloud's queue: sealed, with the sealed answer once the applier has given one.
struct CloudOpRow: Codable, Sendable {
    var id: UUID
    var deviceId: String
    var payload: String
    var result: String?

    enum CodingKeys: String, CodingKey {
        case id, payload, result
        case deviceId = "device_id"
    }
}

/// The Supabase calls cloud sync needs, over its REST APIs: Auth for the account, and PostgREST for the tables and
/// functions in shared/cloud/schema.sql. Every data call carries the user's access token, so row-level security
/// limits it to that account's rows. Only ciphertext is ever sent.
struct CloudClient {
    static let pageSize = 500

    /// Nagster's own project, used unless the user brings theirs. The publishable key is meant to ship in apps: it
    /// only identifies the project, and row-level security keeps every account to its own (encrypted) rows.
    static let defaultProjectURL = "https://zjuvclvuadtiovwvzvzp.supabase.co"
    static let defaultPublishableKey = "sb_publishable_awjhLXnsE7Cy3h89KIeaUw_0dLrr-6A"
    /// Where the email confirmation link sends the browser: back into Nagster, which finishes turning sync on.
    /// It must be in the project's Authentication → URL Configuration → Redirect URLs.
    static let confirmedURL = "nagster://auth-callback"
    /// Said wherever the confirmation email is mentioned: it often lands in spam.
    static let checkSpam = "If it isn't in your inbox, check your spam or junk folder."

    var projectURL: URL
    var publishableKey: String
    var session: URLSession = .shared

    /// "https://abc.supabase.co" from what was typed, or nil. HTTPS only, so tokens and ciphertext never travel in
    /// the clear, except to a project on this device (Supabase's local development stack).
    static func projectURL(from text: String) -> URL? {
        var trimmed = text.trimmingCharacters(in: .whitespaces)
        while trimmed.hasSuffix("/") { trimmed.removeLast() }
        guard !trimmed.isEmpty else { return nil }
        if !trimmed.contains("://") { trimmed = "https://" + trimmed }
        guard let components = URLComponents(string: trimmed), let host = components.host, !host.isEmpty,
              components.scheme == "https" || (components.scheme == "http" && ["localhost", "127.0.0.1"].contains(host)),
              components.path.isEmpty, components.query == nil else { return nil }
        return components.url
    }

    // MARK: - Auth

    /// Creates the account. Nil when the project wants the email confirmed before the first sign-in.
    func signUp(email: String, password: String) async throws -> CloudSession? {
        let redirect = Self.confirmedURL.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? ""
        let reply = try await send("POST", "auth/v1/signup?redirect_to=\(redirect)", body: ["email": email, "password": password])
        return (reply as? [String: Any])?["access_token"] == nil ? nil : try readSession(reply)
    }

    func signIn(email: String, password: String) async throws -> CloudSession {
        do {
            return try readSession(try await send("POST", "auth/v1/token?grant_type=password",
                                                  body: ["email": email, "password": password]))
        } catch CloudError.server(let message) where message.localizedCaseInsensitiveContains("not confirmed") {
            throw CloudError.server("Confirm your email first, with the link Supabase sent you. \(Self.checkSpam)")
        }
    }

    func refresh(_ refreshToken: String) async throws -> CloudSession {
        do {
            return try readSession(try await send("POST", "auth/v1/token?grant_type=refresh_token", body: ["refresh_token": refreshToken]))
        } catch CloudError.server {
            throw CloudError.signInNeeded // revoked, used up or expired: only signing in again helps
        }
    }

    /// Ends the session on the server too. Best effort: the device forgets it either way.
    func signOut(_ cloud: CloudSession) async {
        _ = try? await send("POST", "auth/v1/logout", session: cloud)
    }

    /// Deletes the account: its login, its vault and everything in the cloud. Each device keeps its own alarms.
    func deleteAccount(_ cloud: CloudSession) async throws {
        _ = try await send("POST", "rest/v1/rpc/nagster_delete_account", session: cloud, body: [:])
    }

    // MARK: - The vault

    func vault(_ cloud: CloudSession) async throws -> CloudVault? {
        try decode([CloudVault].self, try await sendData("GET", "rest/v1/nagster_vaults?select=vault_id,kdf_salt,kdf_iterations,wrapped_key",
                                                          session: cloud)).first
    }

    /// Stores a new vault. False if the account already has one (another device got there first).
    func createVault(_ cloud: CloudSession, salt: String, iterations: Int, wrappedKey: String) async throws -> Bool {
        do {
            _ = try await send("POST", "rest/v1/nagster_vaults", session: cloud,
                               body: ["kdf_salt": salt, "kdf_iterations": iterations, "wrapped_key": wrappedKey])
            return true
        } catch CloudError.server(let message) where message.hasPrefix(Self.conflict) {
            return false
        }
    }

    // MARK: - The shared copy, the action queue and the applier role

    /// Records changed after `since`, oldest change first, a page at a time.
    func records(_ cloud: CloudSession, since: Int64) async throws -> [CloudRow] {
        try decode([CloudRow].self, try await sendData(
            "GET", "rest/v1/nagster_records?select=id,kind,seq,deleted,payload&seq=gt.\(since)&order=seq.asc&limit=\(Self.pageSize)", session: cloud))
    }

    /// Sends actions to the queue. One sent before (its reply was lost) is left as it is.
    func submit(_ cloud: CloudSession, _ rows: [CloudOpRow]) async throws {
        guard !rows.isEmpty else { return }
        _ = try await sendData("POST", "rest/v1/nagster_ops?on_conflict=user_id,id", session: cloud,
                               json: try JSONEncoder().encode(rows), prefer: "resolution=ignore-duplicates")
    }

    /// Actions no applier has answered yet, oldest first: from every device, for this one as applier.
    func waitingOps(_ cloud: CloudSession) async throws -> [CloudOpRow] {
        try decode([CloudOpRow].self, try await sendData(
            "GET", "rest/v1/nagster_ops?select=id,device_id,payload,result&result=is.null&order=seq.asc&limit=\(Self.pageSize)", session: cloud))
    }

    /// This device's actions that have been answered.
    func answered(_ cloud: CloudSession, device: String) async throws -> [CloudOpRow] {
        try decode([CloudOpRow].self, try await sendData(
            "GET", "rest/v1/nagster_ops?select=id,device_id,payload,result&device_id=eq.\(device)&result=not.is.null", session: cloud))
    }

    /// Clears answered actions once read.
    func forget(_ cloud: CloudSession, ops: [UUID]) async throws {
        guard !ops.isEmpty else { return }
        let list = ops.map { $0.uuidString.lowercased() }.joined(separator: ",")
        _ = try await sendData("DELETE", "rest/v1/nagster_ops?id=in.(\(list))", session: cloud)
    }

    /// Takes or renews the applier role. False while another device holds it (a running PC always does).
    func claim(_ cloud: CloudSession, device: String, seconds: Int = 60) async throws -> Bool {
        try await send("POST", "rest/v1/rpc/nagster_claim", session: cloud, body: ["device": device, "pc": false, "seconds": seconds]) as? Bool == true
    }

    func release(_ cloud: CloudSession, device: String) async {
        _ = try? await send("POST", "rest/v1/rpc/nagster_release", session: cloud, body: ["device": device])
    }

    /// Publishes the applier's changes and answers.
    /// - Throws: `CloudError.notApplier` if the role was lost (a PC took over, or it ran out).
    func publish(_ cloud: CloudSession, device: String, records: [[String: Any]], answers: [[String: Any]]) async throws {
        do {
            _ = try await send("POST", "rest/v1/rpc/nagster_publish", session: cloud,
                               body: ["device": device, "records": records, "results": answers])
        } catch CloudError.server(let message) where message.contains("not the applier") {
            throw CloudError.notApplier
        }
    }

    // MARK: - The iPhones' settings

    /// The settings shared by devices of one kind, with their seq, or nil if none were saved yet.
    func settings(_ cloud: CloudSession, scope: String) async throws -> (seq: Int64, payload: String)? {
        struct Row: Decodable { var seq: Int64; var payload: String }
        return try decode([Row].self, try await sendData("GET", "rest/v1/nagster_device_settings?select=seq,payload&scope=eq.\(scope)",
                                                         session: cloud)).first.map { ($0.seq, $0.payload) }
    }

    /// Saves them over whatever is there, and returns their new seq.
    func saveSettings(_ cloud: CloudSession, scope: String, payload: String) async throws -> Int64 {
        struct Row: Codable { var scope: String?; var payload: String?; var seq: Int64? }
        let data = try await sendData("POST", "rest/v1/nagster_device_settings?on_conflict=user_id,scope&select=seq", session: cloud,
                                      json: try JSONEncoder().encode(Row(scope: scope, payload: payload)),
                                      prefer: "resolution=merge-duplicates,return=representation")
        guard let seq = try decode([Row].self, data).first?.seq else { throw CloudError.server("Supabase's reply couldn't be read.") }
        return seq
    }

    // MARK: - Plumbing

    private static let conflict = "Already exists"

    private func send(_ method: String, _ path: String, session cloud: CloudSession? = nil, body: [String: Any]? = nil) async throws -> Any? {
        let data = try await sendData(method, path, session: cloud, json: try body.map { try JSONSerialization.data(withJSONObject: $0) })
        return data.isEmpty ? nil : try? JSONSerialization.jsonObject(with: data, options: .fragmentsAllowed)
    }

    private func sendData(_ method: String, _ path: String, session cloud: CloudSession?, json: Data? = nil,
                          prefer: String? = nil) async throws -> Data {
        guard let url = URL(string: projectURL.absoluteString + "/" + path) else { throw CloudError.server("Not a Supabase project URL.") }
        var request = URLRequest(url: url, timeoutInterval: 30)
        request.httpMethod = method
        request.setValue(publishableKey, forHTTPHeaderField: "apikey")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let cloud { request.setValue("Bearer \(cloud.accessToken)", forHTTPHeaderField: "Authorization") }
        if let prefer { request.setValue(prefer, forHTTPHeaderField: "Prefer") }
        if let json {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = json
        }
        let data: Data
        let response: URLResponse
        do { (data, response) = try await session.data(for: request) } catch let error as URLError {
            throw CloudError.unreachable(error.localizedDescription)
        }
        guard let http = response as? HTTPURLResponse else { throw CloudError.unreachable("no reply") }
        if (200...299).contains(http.statusCode) { return data }
        let message = Self.problem(in: data) ?? "Supabase replied \(http.statusCode)."
        switch http.statusCode {
        case 401 where cloud != nil: throw CloudError.signInNeeded
        case 409: throw CloudError.server("\(Self.conflict): \(message)")
        case 404 where path.hasPrefix("rest/"): throw CloudError.notSetUp
        default: throw CloudError.server(message)
        }
    }

    private func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        do { return try JSONDecoder().decode(type, from: data) } catch {
            throw CloudError.server("Supabase's reply couldn't be read.")
        }
    }

    /// The human part of an Auth or PostgREST error reply.
    private static func problem(in data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return ["msg", "error_description", "message", "error"].lazy.compactMap { object[$0] as? String }.first { !$0.isEmpty }
    }

    private func readSession(_ reply: Any?) throws -> CloudSession {
        guard let object = reply as? [String: Any], let access = object["access_token"] as? String,
              let refresh = object["refresh_token"] as? String,
              let user = (object["user"] as? [String: Any])?["id"] as? String else {
            throw CloudError.server("Supabase didn't start a session.")
        }
        let expiresIn = object["expires_in"] as? Double ?? 3600
        return CloudSession(accessToken: access, refreshToken: refresh, expiresAt: Date(timeIntervalSinceNow: expiresIn),
                            userId: user.lowercased())
    }
}
