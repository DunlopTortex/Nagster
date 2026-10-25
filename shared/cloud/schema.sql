-- Nagster cloud sync. Run this once in the Supabase project's SQL editor: Nagster's own project (built into the
-- apps), or one of your own. Running it again is safe.
--
-- The server only ever holds ciphertext: every alarm, occurrence, action and result is encrypted on the device with
-- AES-256-GCM under a random data key, which is stored here only wrapped by a key derived from the user's sync
-- passphrase. See docs/cloud-sync.md.
--
-- How it fits together: the cloud holds the shared copy (nagster_records). Exactly one device at a time, the
-- applier, may change it. The PC is the applier whenever it's running; without one, whichever iPhone syncs takes the
-- role for a moment. Every other device sends what the user did as actions (nagster_ops), which the applier applies
-- with the same rules as syncing over the network, answering each with a result.

create table if not exists public.nagster_vaults (
    user_id uuid primary key default auth.uid() references auth.users (id) on delete cascade,
    -- A new vault (after deleting the account and starting again) gets a new ID, which tells devices to start afresh.
    vault_id uuid not null default gen_random_uuid(),
    format smallint not null default 1 check (format = 1),
    kdf_salt text not null check (length(kdf_salt) between 16 and 64),
    kdf_iterations integer not null check (kdf_iterations >= 600000),
    wrapped_key text not null check (length(wrapped_key) between 40 and 200),
    created_at timestamptz not null default now()
);

-- The shared copy: alarms, occurrences and the PC's settings, one row each. A deleted record keeps its row, with no
-- payload, so every device learns of it.
create table if not exists public.nagster_records (
    user_id uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id uuid not null,
    kind text not null check (kind in ('alarm', 'occurrence', 'settings')),
    -- Bumped on every change; devices pull everything above the last seq they saw.
    seq bigint not null,
    deleted boolean not null default false,
    payload text check (payload is null or length(payload) <= 200000),
    updated_at timestamptz not null default now(),
    primary key (user_id, id),
    check (deleted = (payload is null))
);
create index if not exists nagster_records_by_seq on public.nagster_records (user_id, seq);

-- Actions waiting for the applier, and its answers. A device deletes its own once it has read the answer.
create table if not exists public.nagster_ops (
    user_id uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id uuid not null,
    device_id text not null check (length(device_id) between 1 and 64),
    seq bigint not null default 0,
    payload text not null check (length(payload) <= 200000),
    result text check (result is null or length(result) <= 20000),
    created_at timestamptz not null default now(),
    primary key (user_id, id)
);
create index if not exists nagster_ops_waiting on public.nagster_ops (user_id, seq) where result is null;

-- Who may change the shared copy right now, and until when.
create table if not exists public.nagster_applier (
    user_id uuid primary key default auth.uid() references auth.users (id) on delete cascade,
    device_id text not null,
    is_pc boolean not null,
    expires_at timestamptz not null
);

-- Settings shared by devices of one kind (today the iPhones'), one encrypted row per kind; the newest write wins.
create table if not exists public.nagster_device_settings (
    user_id uuid not null default auth.uid() references auth.users (id) on delete cascade,
    scope text not null check (scope in ('iphone')),
    seq bigint not null default 0,
    payload text not null check (length(payload) <= 20000),
    updated_at timestamptz not null default now(),
    primary key (user_id, scope)
);

create sequence if not exists public.nagster_seq;

-- Numbers new actions and settings in order.
create or replace function public.nagster_stamp_seq()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    new.seq := nextval('public.nagster_seq');
    if tg_table_name = 'nagster_device_settings' then
        new.updated_at := now();
    end if;
    return new;
end;
$$;

drop trigger if exists nagster_ops_seq on public.nagster_ops;
create trigger nagster_ops_seq before insert on public.nagster_ops
    for each row execute function public.nagster_stamp_seq();
drop trigger if exists nagster_device_settings_seq on public.nagster_device_settings;
create trigger nagster_device_settings_seq before insert or update on public.nagster_device_settings
    for each row execute function public.nagster_stamp_seq();

alter table public.nagster_vaults enable row level security;
alter table public.nagster_records enable row level security;
alter table public.nagster_ops enable row level security;
alter table public.nagster_applier enable row level security;
alter table public.nagster_device_settings enable row level security;

-- Each account reaches only its own rows. Records, answers and the applier role change only through the functions
-- below, which check the caller holds the role; there is deliberately no direct way to write them.
drop policy if exists "own vault" on public.nagster_vaults;
create policy "own vault" on public.nagster_vaults for all to authenticated
    using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
drop policy if exists "read own records" on public.nagster_records;
create policy "read own records" on public.nagster_records for select to authenticated
    using (user_id = (select auth.uid()));
drop policy if exists "own ops" on public.nagster_ops;
create policy "own ops" on public.nagster_ops for all to authenticated
    using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()) and result is null);
drop policy if exists "read own applier" on public.nagster_applier;
create policy "read own applier" on public.nagster_applier for select to authenticated
    using (user_id = (select auth.uid()));
drop policy if exists "own device settings" on public.nagster_device_settings;
create policy "own device settings" on public.nagster_device_settings for all to authenticated
    using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));

revoke all on public.nagster_vaults, public.nagster_records, public.nagster_ops, public.nagster_applier,
    public.nagster_device_settings from anon;
grant select, insert, delete on public.nagster_vaults to authenticated;
grant select on public.nagster_records, public.nagster_applier to authenticated;
grant select, insert, delete on public.nagster_ops to authenticated;
grant select, insert, update, delete on public.nagster_device_settings to authenticated;

-- Takes (or renews) the applier role for `seconds`. It's granted when nobody holds it, it has run out, the caller
-- already holds it, or the caller is a PC and the holder isn't: a PC that starts up takes over from an iPhone.
create or replace function public.nagster_claim(device text, pc boolean, seconds integer)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
    uid uuid := auth.uid();
    holder public.nagster_applier;
begin
    if uid is null then
        raise exception 'sign in first' using errcode = '42501';
    end if;
    if seconds not between 10 and 300 then
        raise exception 'seconds must be from 10 to 300' using errcode = '22023';
    end if;
    perform pg_advisory_xact_lock(hashtextextended('nagster:' || uid::text, 0));
    select * into holder from public.nagster_applier where user_id = uid for update;
    if found and holder.expires_at > now() and holder.device_id <> device and not (pc and not holder.is_pc) then
        return false;
    end if;
    insert into public.nagster_applier (user_id, device_id, is_pc, expires_at)
    values (uid, device, pc, now() + make_interval(secs => seconds))
    on conflict (user_id) do update set device_id = excluded.device_id, is_pc = excluded.is_pc,
        expires_at = excluded.expires_at;
    return true;
end;
$$;

-- Gives the role up early, if the caller holds it.
create or replace function public.nagster_release(device text)
returns void
language sql
security definer
set search_path = ''
as $$
    delete from public.nagster_applier where user_id = auth.uid() and device_id = device;
$$;

-- The applier's changes, in one go: records ([{ id, kind, deleted, payload }]) and answers to actions
-- ([{ id, result }]). Refused unless the caller holds the role, so two devices can never both change the copy.
-- Returns [{ id, seq }] for the records.
create or replace function public.nagster_publish(device text, records jsonb, results jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    uid uuid := auth.uid();
    record jsonb;
    answer jsonb;
    new_seq bigint;
    seqs jsonb := '[]'::jsonb;
begin
    if uid is null then
        raise exception 'sign in first' using errcode = '42501';
    end if;
    if jsonb_typeof(records) is distinct from 'array' or jsonb_typeof(results) is distinct from 'array'
        or jsonb_array_length(records) > 1000 or jsonb_array_length(results) > 1000 then
        raise exception 'records and results must be lists of at most 1000' using errcode = '22023';
    end if;
    perform pg_advisory_xact_lock(hashtextextended('nagster:' || uid::text, 0));
    if not exists (select 1 from public.nagster_applier
                   where user_id = uid and device_id = device and expires_at > now()) then
        raise exception 'not the applier' using errcode = 'P0001', hint = 'claim the applier role first';
    end if;
    for record in select value from jsonb_array_elements(records) loop
        new_seq := nextval('public.nagster_seq');
        insert into public.nagster_records (user_id, id, kind, seq, deleted, payload)
        values (uid, (record ->> 'id')::uuid, record ->> 'kind', new_seq, coalesce((record ->> 'deleted')::boolean, false),
                case when coalesce((record ->> 'deleted')::boolean, false) then null else record ->> 'payload' end)
        on conflict (user_id, id) do update set kind = excluded.kind, seq = excluded.seq, deleted = excluded.deleted,
            payload = excluded.payload, updated_at = now();
        seqs := seqs || jsonb_build_object('id', record ->> 'id', 'seq', new_seq);
    end loop;
    for answer in select value from jsonb_array_elements(results) loop
        update public.nagster_ops set result = answer ->> 'result'
        where user_id = uid and id = (answer ->> 'id')::uuid and result is null;
    end loop;
    return seqs;
end;
$$;

-- Deletes the signed-in account: its login, and with it (on delete cascade) everything above. Each device keeps its
-- own alarms.
create or replace function public.nagster_delete_account()
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if auth.uid() is null then
        raise exception 'sign in first' using errcode = '42501';
    end if;
    delete from auth.users where id = auth.uid();
end;
$$;

revoke all on function public.nagster_stamp_seq() from public, anon, authenticated;
revoke all on function public.nagster_claim(text, boolean, integer), public.nagster_release(text),
    public.nagster_publish(text, jsonb, jsonb), public.nagster_delete_account() from public, anon;
grant execute on function public.nagster_claim(text, boolean, integer), public.nagster_release(text),
    public.nagster_publish(text, jsonb, jsonb), public.nagster_delete_account() to authenticated;
