# External-engine connector protocol v1 vectors

This directory is the stable interoperability contract for protocol `"1"`. Control is the source of truth. The future `valence-works/elsa-control-connector` repository consumes these files so a net8.0 reimplementation cannot drift from `ElsaControl.Deployment.Core`.

Do not hand-edit `vectors.json`. Regenerate it from Control's protocol code:

```bash
ELSA_CONTROL_REGENERATE_CONNECTOR_PROTOCOL_VECTORS=1 \
  dotnet test tests/Deployment/ElsaControl.Deployment.Core.Tests/ElsaControl.Deployment.Core.Tests.csproj \
  --filter FullyQualifiedName~ExternalEngineConnectorProtocolGoldenVectorTests
```

CI regenerates the document from the live protocol types and diffs it against the committed file. A protocol-code change that alters canonical bytes, domains, digests, or verification results fails until this file is regenerated.

## Files

| File | Role |
|---|---|
| `vectors.json` | Golden enroll/redeem, heartbeat, rotate, and revoke vectors |

## Document shape

`vectors.json` is UTF-8 JSON without a BOM, LF newlines, two-space indent, and a trailing newline. Property order is part of the contract.

Top-level fields:

| Field | Meaning |
|---|---|
| `schemaVersion` | Vector-document schema. Currently `1`. |
| `protocolVersion` | Connector protocol spoken on the wire. `"1"`. |
| `generatedFrom` | Control assembly that produced the bytes. |
| `algorithms` | Key, curve, signature encoding, hash, and field canonicalization. |
| `clock` | Fixed `now` plus Control's proof age and 30-second future-skew window. |
| `keys` | Test-only current and next P-256 keys. The object itself is marked `testOnly: true`. `privateKeyPkcs8Base64Url` is PKCS#8, `publicKeySpkiBase64Url` is DER SubjectPublicKeyInfo. Both are unpadded base64url. Never use these keys outside the vectors. |
| `scope` | Fixed organization, workspace, connection, identity, and challenge identifiers. |
| `vectors` | One object per signed message. |

Each vector:

| Field | Meaning |
|---|---|
| `id` | Stable identifier (`<kind>.<case>`). |
| `kind` | `enroll-redeem`, `heartbeat`, `rotate`, or `revoke`. |
| `description` | Human-readable intent. |
| `domain` | First canonical field. |
| `inputs` | Values used to build the signed message. Heartbeat includes the report object that `CreateCanonicalPayload` encodes. |
| `canonicalPayloadHex` | Uppercase hex of the exact bytes Control signs. For heartbeat this is the **proof** message, not the report JSON. |
| `reportCanonicalPayloadHex` | Heartbeat only. Uppercase hex of the compact UTF-8 report JSON. |
| `reportCanonicalPayloadUtf8` | Heartbeat only. The same report JSON as a UTF-8 string. |
| `payloadDigest` | Unpadded base64url SHA-256 over the operation-specific payload (`CreateRotationPayloadDigest`, `CreateRevocationPayloadDigest`, or heartbeat report digest). Redeem input uses `challengeHash` and `publicKeyThumbprint` instead. |
| `signatureBase64Url` | IEEE P1363 `r \|\| s` signature over `canonicalPayloadHex`, unpadded base64url, 64 decoded bytes. |
| `expected.signatureValid` | Result of `ExternalEngineEnrollmentProtocol.Verify` on the current public key, canonical payload, and signature. |

## How a connector must use these vectors

1. Rebuild each `canonicalPayloadHex` with the same length-prefixed UTF-8 field encoding (`uint32be` length then bytes; domain first).
2. For heartbeat, also rebuild `reportCanonicalPayloadUtf8` with the protocol-v1 property order and compare both the JSON bytes and the proof digest. Required `runnerId` is unpadded base64url of 16 to 32 random bytes and is written immediately after `connectorVersion`. Optional `displayName` is omitted when absent and written immediately after `runnerId` when present. `heartbeat.valid-display-name` covers a plain ASCII label; `heartbeat.valid-non-ascii-display-name` locks `JavaScriptEncoder.Default` escaping (`é` → `\u00E9`, `&` → `\u0026`, `π` → `\u03C0`). Rotation canonical fields are domain, next public-key thumbprint, overlap ticks, then `runnerId`.
3. Verify each `signatureBase64Url` with the published current public key and SHA-256 / P-256 / IEEE P1363.
4. Assert `expected.signatureValid`. Invalid vectors are well-formed 64-byte signatures that must not verify.

The private keys are **test fixtures**, not production secrets. They exist so Control can sign the committed vectors with the same `Sign` helper the product uses.

## Clock rules these vectors document

- Proof `issuedAt` is Unix milliseconds inside the signed proof message.
- Control accepts `issuedAt` up to 30 seconds ahead of its clock and up to five minutes behind it.
- Single-use nonce consumption and strictly increasing heartbeat sequence numbers stay in force after signature verification. These vectors cover the signed messages only.
