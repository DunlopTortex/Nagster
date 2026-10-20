import CryptoKit
import Foundation

/// Derives an occurrence's ID from its alarm and scheduled time, exactly as the Windows app's
/// `AlarmEngine.OccurrenceId` does, so an occurrence the phone works out for itself has the ID the PC
/// will give it: a version-5-style UUID over SHA-1 of "{alarm id}|{yyyy-MM-ddTHH:mm:ss}".
public enum OccurrenceID {
    public static func make(definitionID: UUID, scheduledFor: WallTime) -> UUID {
        let name = "\(definitionID.uuidString.lowercased())|\(scheduledFor.iso)"
        var bytes = Array(Insecure.SHA1.hash(data: Data(name.utf8)).prefix(16))
        bytes[6] = (bytes[6] & 0x0F) | 0x50
        bytes[8] = (bytes[8] & 0x3F) | 0x80
        return UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
                           bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]))
    }
}
