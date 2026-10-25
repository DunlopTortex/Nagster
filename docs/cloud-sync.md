# Cloud sync (optional)

Cloud sync keeps Nagster on your iPhones and on your PC in step from anywhere, without being on the same network or
using Tailscale. It works with a PC or without one. It's off until you turn it on, on each device, and runs on
Nagster's own [Supabase](https://supabase.com) project, built into the apps; you can use a Supabase project of your
own instead.

Everything is **end-to-end encrypted**: each device encrypts every alarm, occurrence and action with a key that only
your devices have, before anything leaves them. The cloud stores ciphertext. Nobody can read your alarms from there:
not Nagster, not Supabase, not anyone who gets into the project.

## Setting it up

1. On your first device, open cloud sync in Settings (**Settings → Cloud sync** on the PC, **Settings → Cloud sync
   (optional)** on the iPhone).
2. Enter your email and choose an account password and a **sync passphrase**: at least 12 characters, ideally a few
   unrelated words. Type the passphrase twice and press **Create account**.
3. Open the confirmation link Supabase emails you. **If it isn't in your inbox, check your spam or junk folder.** The
   link opens Nagster, which finishes turning cloud sync on. If you open it on another device, or after closing
   Nagster, it just confirms the address: press **Turn on** afterwards.
4. On every other device, enter the same email, password and passphrase, and press **Turn on**. Your other iPhones
   and iPads can skip this (see below).

The account password signs you in; the passphrase is what encrypts. They're separate on purpose: a password can be
reset by email, but nothing can reset the passphrase.

**Keep the passphrase safe.** It isn't stored anywhere except, as a key derived from it, on your devices. If you
forget it, the cloud copy can't be decrypted by anyone, you included. The alarms on each device are unaffected:
delete the account and start again with a new passphrase.

On the iPhone, cloud sync replaces syncing with the PC over your network: turn on Web access on the PC only if you
still want its web interface. The PC can keep serving iPhones that aren't on cloud sync.

### Your other Apple devices

With **Set up my other Apple devices too** on (the default) when you turn cloud sync on, the iPhone puts the account
into iCloud Keychain: the email and password, and the key that opens your alarms. Any other iPhone or iPad signed in
to the same Apple Account with iCloud Keychain on finds it when Nagster opens, signs in with a session of its own,
and syncs everything, settings included, with nothing to type. iCloud Keychain is end-to-end encrypted by Apple. The
PC can't read iCloud Keychain, so it still asks for the passphrase.

**Turn off** or **Delete account…** on any iPhone removes it from iCloud Keychain, so no device joins by itself again;
devices already syncing carry on until turned off themselves.

### Settings

The iPhone's own settings (how it nags, its alarm hours, its snooze choices and the theme) are shared by every iPhone
on the account, encrypted like everything else, the newest change winning; a device joining takes the settings the
others already share. The PC keeps its own settings, and its snooze choices, automatic snooze and response time reach
the iPhones as they do over the network. Pausing alerts stays per device.

### Using your own Supabase project

Tick **Use my own Supabase project** before turning cloud sync on, on every device:

1. Create a project at [supabase.com](https://supabase.com).
2. In the project's **SQL editor**, run [`shared/cloud/schema.sql`](../shared/cloud/schema.sql) once. It creates the
   tables and functions, locked down so each account can only reach its own rows.
3. From **Project Settings → API**, enter the **project URL** (`https://abcd.supabase.co`) and the **publishable**
   (anon) key. Never use the secret (service role) key; Nagster doesn't need it.
4. Under **Authentication → URL Configuration**, add `nagster://auth-callback` to **Redirect URLs**, so the
   confirmation link opens Nagster.

## How it works

The cloud holds one shared copy of your alarms and their occurrences, and a queue of actions (completed, dismissed,
snoozed, rescheduled, added, edited or deleted an alarm). Only one device at a time, the **applier**, may change the
shared copy; every other device sends what you did to the queue, and the applier applies it.

- **With a PC:** the PC is the applier whenever Nagster is running on it. Every 15 seconds it applies the queued
  actions with exactly the rules described in [syncing](sync.md#how-conflicts-are-settled), and publishes what
  changed, including what it did itself (generating occurrences, showing popups, automatic snoozes). Your iPhone
  shows your action straight away and keeps nagging from its own copy, as always; the PC's answer arrives on the
  next sync, a few seconds later.
- **Without a PC** (it's off, or you don't have one): whichever iPhone syncs takes the applier role for a moment and
  applies everyone's queued actions itself, with the same rules. Changes reach your other iPhones when they next sync.
- **When the PC starts again** it takes the role back straight away, takes in whatever the iPhones did meanwhile,
  and carries on. A finished occurrence on the PC never reopens.

Cloud sync works from one PC per account at a time.

## Turning it off

- **Turn off** forgets the account on that device. The alarms there stay as they are, and so does the cloud copy.
- **Delete account…** deletes your account for good: the login, the vault and everything in the cloud. Each device
  turns cloud sync off when it next tries to sync, and keeps its alarms.

## The encryption

| Piece                  | How                                                                                       |
| ---------------------- | ----------------------------------------------------------------------------------------- |
| Data key               | 256 random bits, made once by your first device                                           |
| Records, actions, answers | AES-256-GCM under the data key, a fresh random 96-bit nonce each time                  |
| Binding                | The account and record IDs are authenticated with each, so ciphertext can't be moved      |
| Sizes                  | Padded to a multiple of 256 bytes                                                          |
| Passphrase → key       | PBKDF2-HMAC-SHA256, 600,000 iterations, random 128-bit salt (OWASP's current advice)      |
| Data key in the cloud  | Only wrapped (AES-256-GCM) by the passphrase's key, in the `nagster_vaults` table         |
| Data key on the device | iPhone: the Keychain, this device only; also iCloud Keychain if you set up your other Apple devices. PC: DPAPI, for your Windows user |
| Transport              | HTTPS only; the apps refuse a plain `http://` project URL                                 |

**What the cloud can see:** your email, how many alarms, occurrences and actions you have, their random IDs, when
each changed, their sizes to the nearest 256 bytes, and which device is applying changes. Nothing about what your
alarms say or when they go off.

Things the encryption can't prevent, worth knowing:

- A weak passphrase is the weak point. Someone holding the vault can try guesses offline, slowed by PBKDF2 but not
  stopped. Twelve characters is the minimum; four or five random words is much better.
- Whoever runs the project (Nagster, for the built-in one) can see the metadata above, and can delete rows or hold
  back recent changes. They can't read or forge anything: altered or substituted ciphertext fails to decrypt, and
  sync stops with an error rather than using it.

Format: each sealed value is base64 of a version byte (1), the 12-byte nonce, the ciphertext and the 16-byte tag.
The associated data is `nagster/vault/v1/<user>` for the wrapped key, and `nagster/record/v1/<user>/<id>`,
`nagster/op/v1/<user>/<id>`, `nagster/result/v1/<user>/<id>` and `nagster/settings/v1/<user>/iphone` for the rest
(lower-case UUIDs). Records, actions and answers are the same JSON as [network sync](sync.md#the-protocol) carries,
padded with spaces. [`shared/cloud/crypto-vectors.json`](../shared/cloud/crypto-vectors.json) pins it; both apps'
tests check against it.

## The protocol

All through PostgREST, on the tables and functions in [`shared/cloud/schema.sql`](../shared/cloud/schema.sql):

- **Pull** `nagster_records` with `seq` above the last one seen, 500 a page. A record's seq is what an action names as
  its base revision. A deleted record keeps its row, with no payload.
- **Send actions** to `nagster_ops`, one sealed row each, keyed by the action's ID so a resend is ignored.
- **Apply** (the applier): `nagster_claim(device, pc, seconds)` takes or renews the role, refused while another device
  holds it, except that a PC always takes it from an iPhone. Then read the unanswered actions, apply them, and send
  the changed records and the sealed answers in one `nagster_publish(device, records, results)`, refused unless the
  caller still holds the role. `nagster_release(device)` gives it up early.
- **Collect answers**: a device reads its own answered actions, then deletes them.
- The PC numbers records by its own revisions. It remembers, for each record, the seq it was published at and its
  revision then, and turns an action's base from one into the other.
