# External-engine connector rehearsal

This is a proof-only connector driver for the external-engine protocol. It is not a supported production connector and does not install a service beside Elsa. It makes outbound HTTPS requests only to the Control runtime routes and has no Azure, Kubernetes, Docker, or infrastructure mutation code.

The driver reads the one-time setup bundle from standard input, creates P-256 keys in memory, and emits JSON Lines evidence containing timestamps, stable IDs, state names, HTTP status codes, key versions, and heartbeat sequence numbers. It never emits the setup challenge, keys, signatures, nonces, request bodies, response bodies, tokens, or Studio URL.

## Safe invocation

Build it before creating a pairing:

```bash
dotnet build scripts/external-engine-rehearsal/ExternalEngineRehearsal.csproj
dotnet test scripts/external-engine-rehearsal/tests/ExternalEngineRehearsal.Tests.csproj
```

Create a connection in Elsa Cloud, choose **Copy setup bundle**, then run the driver and paste the bundle into standard input. End input with Ctrl-D. Do not put the bundle in command arguments, shell history, URLs, logs, screenshots, or committed files.

```bash
dotnet run --project scripts/external-engine-rehearsal/ExternalEngineRehearsal.csproj -- \
  --studio-url https://customer-studio.example/ \
  --hold-seconds 120
```

The run redeems and authenticates, proves unsupported-protocol handling, submits a healthy heartbeat, proves replay rejection, optionally pauses for stale-state browser observation, reconnects, rotates its key, revokes it, and proves the revoked identity is rejected. A hold of at least 91 seconds is required to observe the 90-second stale threshold. During the hold, confirm the Studio candidate in Cloud and verify that Studio performs its own customer authentication; Cloud does not provide Studio SSO.

Omit `--studio-url` for the private-network/outbound-only case. Run it from the private customer network with inbound Control access denied and permit only outbound HTTPS to the published Control endpoint. The driver then advertises `connection.status` only and never asks Control or the browser to probe the private engine.

To prove pairing expiry, keep an unredeemed bundle until its displayed expiry has passed, then run:

```bash
dotnet run --project scripts/external-engine-rehearsal/ExternalEngineRehearsal.csproj -- --expect-expired
```

The driver refuses to send in this mode while the bundle is still live. Its evidence establishes that an already elapsed bundle was denied; the public runtime route deliberately returns a generic denial rather than disclosing the server's exact authentication reason.

## Support runbook

1. **Pairing does not redeem:** confirm the bundle is unexpired and was copied as one intact JSON value. Never request the raw bundle in a ticket. Ask for the safe event name, timestamp, HTTP status, and connection ID. Create a fresh pairing after expiry or suspected disclosure.
2. **Connector is offline or stale:** confirm the connector process can resolve and reach the Control HTTPS endpoint outbound. Check its clock and TLS trust. Collect only the last safe evidence timestamp/status and the Cloud connection ID. Do not collect private keys, signatures, nonces, challenges, tokens, raw bodies, or provider responses.
3. **Unsupported connector:** upgrade the connector to a version that speaks protocol `1`. An unsupported runtime image is diagnostic metadata; pairing does not grant image or infrastructure authority.
4. **Studio does not open:** the connector must advertise `studio.open`, report an HTTPS Studio candidate, and a setup manager must confirm that candidate. The customer remains responsible for Studio reachability and authentication. Elsa Cloud does not bypass Studio login.
5. **Repair or re-pair:** use Cloud's repair flow to revoke the current identity and issue a fresh bundle. Generate a new key; Control does not escrow connector keys. A pre-revocation challenge cannot recover a revoked connection.
6. **Disconnect:** revoke in Cloud or let this rehearsal complete its revocation phase. Disconnect removes Control access and Studio metadata only. It does not stop, change, or delete the customer engine or its resources.
7. **Escalate:** provide the stable connection/identity IDs, UTC timestamps, safe event names, HTTP status codes, connector version, protocol version, and redacted network-policy evidence. Keep every secret and raw body out of the escalation.

## No-resource-mutation evidence

For an Azure-backed customer rehearsal, capture a redacted inventory fingerprint immediately before and after the run from the customer's normal inventory tooling. Compare resource IDs, resource types, locations, and provisioning states; do not export configuration values or secrets. The connector driver itself has no Azure SDK/CLI dependency and calls only these Control paths:

- `/api/runtime/external-engine-connections/{connectionId}/enrollment/redeem`
- `/api/runtime/external-engine-connections/{connectionId}/authenticate`
- `/api/runtime/external-engine-connections/{connectionId}/heartbeat`
- `/api/runtime/external-engine-connections/{connectionId}/identity/rotate`
- `/api/runtime/external-engine-connections/{connectionId}/identity/revoke`

Archive the value-free JSON Lines output, the before/after fingerprint result, Control and Cloud commit SHAs, and desktop/mobile screenshots that contain no setup bundle. Delete any temporary clipboard entry or local capture after the run.
