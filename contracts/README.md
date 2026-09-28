# Cross-repository contracts

Files here are shared, byte for byte, with [`valence-works/elsa-production-image`](https://github.com/valence-works/elsa-production-image)
under the same path. Each repository's tests assert that its code matches the file and that the file's canonical digest
equals a constant pinned in both repositories, so neither side can change the contract alone.

- `managed-elsa-studio-grants-v1.json`: the runtime permissions Control may issue to a workspace Owner through managed
  Open Studio, and the release-manifest capability that advertises support for them. The image rejects any other
  permission at redemption. Changing the list means a new capability version (`-v2`) in the image and Control together.
