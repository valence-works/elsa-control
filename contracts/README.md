# Cross-repository contracts

Files here are shared, byte for byte, with [`valence-works/elsa-production-image`](https://github.com/valence-works/elsa-production-image)
under the same path. Each repository's tests assert that its code matches the file and that the file's canonical digest
equals a constant pinned in both repositories, so neither side can change the contract alone.

- `managed-elsa-studio-grants-v1.json`: Elsa 3.8 `verb:resource` permissions Control may issue to a workspace Owner
  through managed Open Studio, and the release-manifest capability that advertises support for them. The image rejects
  any other permission at redemption.
- `managed-elsa-studio-grants-v2.json`: Elsa 3.9 `resource:verb` permissions for the same handoff. Descriptor-catalog
  and installed-features reads that 3.9 opened to any signed-in user are omitted. Changing either list means a new
  capability version in the image and Control together.

## Version selection

The image picks a contract from its own **release line**, falling back to the catalog or manifest version when the
line is missing. Control issue and redeem use the provider-confirmed deployment's release (the same identity the
image was deployed with), not the next resolved plan. A missing confirmed release keeps v1. This is per runtime,
not a global switch.

| Runtime release line (or catalog / manifest version) | Capability the image must declare | Grant set Control issues and the image allowlists |
| --- | --- | --- |
| 3.8 and older (`3.8`, `3.7`, `2.x`, …) | `managed-elsa-studio-grants-v1` | `managed-elsa-studio-grants-v1.json` |
| 3.9 and newer (`3.9`, `3.10`, `4.1`, …) | `managed-elsa-studio-grants-v2` | `managed-elsa-studio-grants-v2.json` |

Rules the image repo follows:

1. Parse the release line as `{major}.{minor}`. If the line is absent, use the first two numeric segments of the
   catalog or manifest version (`3.9.0` and `3.9.0-preview.1` are both line `3.9`).
2. Compare numerically: `(major, minor) >= (3, 9)` selects v2; anything older, or an unparseable identity, selects v1.
3. Declare exactly one Studio-grants capability, and it must be the one this table names for that line. A 3.9 image
   that still advertises v1 is not Studio-grant capable: Elsa 3.9 ignores the old strings.
4. Configure the runtime allowlist to the matching contract's `permissions` array. Control sends that same list when
   the admitted image declared the required capability, or the single structured-logs read from that contract when
   it did not.
