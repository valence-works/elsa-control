# External engine connector enrollment protocol

**Status:** protocol and domain foundation for [#488](https://github.com/valence-works/elsa-control/issues/488)

**Parent:** [#480](https://github.com/valence-works/elsa-control/issues/480)

This document defines the cryptographic and domain contract for pairing one customer-operated Elsa engine connector with one Elsa Control external-engine connection. It does not expose an HTTP route, authorize a runtime command, prove Azure ownership, or grant infrastructure reconciliation authority. Durable storage, replay-resistant request authentication, rotation, and revocation remain the gated follow-up work in #489 and #490.

## Trust boundary

A workspace setup manager may issue a one-time pairing challenge for one organization, workspace, and connection. The challenge is bearer enrollment authority until it is redeemed or expires. A connector running beside the customer-operated engine creates its own ECDSA P-256 key pair, keeps the private key locally, and signs the bound redemption message. Control stores the challenge hash and connector public key; it never receives the connector private key.

Successful redemption proves two facts only:

1. the redeemer possessed the one-time challenge; and
2. the redeemer possessed the private key corresponding to the enrolled public key.

It does not prove that the connector controls Azure, Kubernetes, Docker, the host, the engine's business data, or a governed Valence Runtime image. It grants no deployment or runtime-control capability. #481 may later expose narrow pairing routes, #482 may accept authenticated runtime facts, and #484 must separately authorize each mutation capability.

The identity is scoped to one external engine connection. It is separate from the proposed customer infrastructure agent in ADR-0011 and #103/#316. The two paths may reuse reviewed cryptographic and replay primitives, but never share identity scopes or infer one another's authority.

## Algorithms and encodings

- Challenge: 32 random bytes from the platform cryptographic RNG, base64url without padding.
- Stored challenge value: base64url of SHA-256 over the decoded 32 challenge bytes.
- Connector key: ECDSA on NIST P-256, public key encoded as DER SubjectPublicKeyInfo and then base64url without padding.
- Public-key thumbprint: base64url of SHA-256 over the exact DER SubjectPublicKeyInfo bytes.
- Signature: ECDSA with SHA-256 in the fixed-width IEEE P1363 `r || s` form, base64url without padding.
- Text: UTF-8. Identifiers use lowercase hyphenated GUID form. Timestamps in connector proofs use Unix milliseconds.

P-256/SHA-256 aligns with the NIST Digital Signature Standard and its named-curve guidance. The replay and sender-binding properties intentionally follow the security shape described by [RFC 9449](https://www.rfc-editor.org/rfc/rfc9449), while this protocol is not OAuth DPoP and does not claim wire compatibility with it. The 32-byte random challenge and SHA-256 verifier pattern follow the entropy guidance in [RFC 7636](https://www.rfc-editor.org/rfc/rfc7636).

Algorithm agility is fail closed. Version 1 accepts exactly `ECDSA-P256-SHA256`; another curve, signature encoding, hash, canonicalization version, or key version requires an explicit protocol version and downgrade review.

## Scope binding

Control derives these values rather than accepting a caller-selected authority:

- purpose: `external-engine.pair`
- audience: `urn:elsa:external-engine-connector:{connectionId:D}`
- initial key version: `1`

The challenge record also binds organization ID, workspace ID, connection ID, issue time, and expiry. The lifetime defaults to ten minutes and may never exceed fifteen minutes.

## Canonical messages

Every signed message is a sequence of fields. Each field is encoded as a four-byte unsigned big-endian byte length followed by its UTF-8 bytes. Length-prefixing prevents delimiter ambiguity. Field order is normative.

### Redemption message v1

Domain: `elsa-control.external-engine-enrollment.redeem.v1`

1. domain
2. challenge ID
3. organization ID
4. workspace ID
5. connection ID
6. purpose
7. audience
8. challenge hash
9. connector public-key thumbprint

The connector signs this message with the private key whose public half appears in field 9. Control verifies the signature before it attempts atomic challenge consumption. The durable store must then compare every scope field, compare the challenge hash in fixed time, reject expiry/replay, consume the challenge, and create the identity in one transaction.

### Connector proof message v1

Domain: `elsa-control.external-engine-connector.proof.v1`

1. domain
2. connector identity ID
3. organization ID
4. workspace ID
5. connection ID
6. audience
7. key version
8. operation
9. SHA-256 payload digest
10. issued-at Unix milliseconds
11. nonce

The domain foundation verifies the signature and exact expected operation/payload binding. It deliberately does not authorize an HTTP route yet. #490 must enforce a short timestamp window and atomically consume each nonce before any proof can authorize a request. The route layer must bind `operation` to its exact method/path semantic and compute the payload digest from the received canonical body; it must never trust caller-provided expected values.

## Lifecycle

1. **Issue:** validate organization/workspace/connection scope, generate the challenge, store only its hash and safe bindings, return the raw challenge once.
2. **Redeem:** validate exact purpose/audience, validate P-256 public key, verify the signed redemption message, then atomically consume the challenge and create key version 1.
3. **Use:** after #490, verify an operation-bound proof, timestamp, nonce, identity state, and key version on every connector request.
4. **Rotate:** after #490, the current key signs a transition to the new key. The old key has one bounded overlap window that cannot be extended by replay.
5. **Revoke:** after #490, mark the identity revoked atomically. The next otherwise-valid proof fails.
6. **Recover:** loss of the private key requires revocation and a fresh pairing challenge. Control never escrows or restores connector private keys.

## Threat analysis

| Threat | V1 response | Remaining gate |
|---|---|---|
| Challenge database disclosure | Only a high-entropy SHA-256 hash is stored; offline guessing is impractical. | #489 schema and audit tests must prove raw values cannot be stored. |
| Challenge theft before redemption | Short expiry, single use, exact connection scope, secure setup handling. The thief can race the legitimate connector because the challenge is bearer enrollment authority. | UI/API must warn against disclosure; a future pre-bound key ceremony would be a new protocol version. |
| Replay or concurrent redemption | Store contract requires atomic consume-and-create; the in-memory reference implementation demonstrates one winner. | #489 must prove database concurrency on supported stores. |
| Cross-organization/workspace/connection redemption | Every signed and stored binding is compared exactly. | #489 persistence and #481 route authorization tests. |
| Captured challenge after pairing | The challenge is consumed; later messages require the enrolled private key. | #490 timestamp and nonce replay enforcement. |
| Connector private-key exfiltration | Control never receives or stores it. | Customer host security; revoke and re-pair on compromise. |
| Public-key substitution | Redemption signature covers the public-key thumbprint. | Challenge theft before redemption remains as described above. |
| Proof replay | Operation, payload, timestamp, and nonce are signed. | Not accepted as route authorization until #490 durably consumes nonces and enforces time. |
| Confused deputy | Purpose, audience, organization, workspace, connection, operation, and payload are signed/bound. | Route layer must supply exact expected operation and digest. |
| Downgrade | Only protocol v1, P-256/SHA-256, fixed signature encoding, and key version 1 are admitted here. | #490 defines version transition/rotation rules. |
| Offline connector | No inbound connectivity is required. | #490 documents expiry/reconnect and fresh pairing recovery. |
| Broader infrastructure privilege | Identity type and audience are external-engine specific and grant no command scope. | #484 separately gates future deployment/control operations. |

## Data handling rules

Raw challenges, private keys, proof signatures, request nonces, authorization headers, and raw payloads are never audit or diagnostic fields. Safe audit data may include stable IDs, event type, key version, public-key thumbprint, safe reason code, and timestamp. Read models must not expose challenge hashes or public keys unless a later reviewed protocol requires them.

The one-time issue result and redemption/proof request types redact challenge, public key, signature, and nonce values from their string representation. This reduces accidental logging risk; callers remain responsible for excluding request bodies from telemetry.

## Delivery gates

- #488 supplies this reviewed contract, canonical encoders, P-256 verification, hash-only store contract, an in-memory reference implementation, and domain tests. Its connector-signature verifier is a cryptographic primitive, not route authorization.
- #489 supplies durable EF storage, migrations, atomic redemption, workspace isolation, and safe audit.
- #490 supplies timestamp/nonce replay defense, bounded rotation, immediate revocation, and offline recovery behavior.
- #481 may expose routes only after the relevant #489/#490 guarantees exist and must use narrow workspace/BFF authorization.
