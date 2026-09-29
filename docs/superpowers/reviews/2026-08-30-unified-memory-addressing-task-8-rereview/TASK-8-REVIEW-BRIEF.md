# Task 8 Review Brief

## Contract under review

Task 8 requires bundle export/import to carry logical addresses, metadata,
content, and portable opaque artifact keys. Import must validate untrusted
address/key data before writes, discard source physical identity, regenerate
local key/path associations, and preserve existing bundle-version and legacy
metadata rules where permitted.

Relevant plan steps are Task 8 steps 1–5, especially:

- no absolute, drive, UNC, host, or `file://` values in exported JSON;
- complete-import refusal for rooted, URI-shaped, traversal, or mixed-separator
  artifact keys before any topic write;
- preservation of all `ArtifactEntry` members except regenerated physical
  identity;
- legacy physical-location metadata must not survive import/export;
- legacy address-led entries must remain importable through the typed address
  boundary.

## Fix-range delta to verify

The fix range makes three focused changes:

1. `BundleFormatService` no longer rejects an entry solely because its legacy
   `Name` is absent/empty; it requires a valid deserialized `Address` and then
   derives the subject from that address.
2. `BundleLegacyKeys` expands the case-insensitive set of legacy physical
   location carriers to include `uri`, `artifactUri`, `artifactPath`,
   `filePath`, `physicalPath`, `physicalUri`, `path`, and `location`.
3. `BundleArtifactKeyPortabilityTests` parameterizes the physical-metadata
   test over those keys and their uppercase spellings.

## Re-review questions

### Correctness

- Does an address-led modern entry import when `Name` is absent, while a
  missing/invalid address still fails before any write?
- Does the derived subject remain the canonical logical address expected by the
  target store and the artifact lookup path?
- Does removing the `Name` check accidentally admit a malformed address or an
  entry whose later code still depends on `Name`?
- Does the expanded key list remove every intended physical carrier without
  deleting unrelated unknown metadata?
- Are case-insensitive variants actually covered by the test data and by the
  production removal loop?

### Security and data integrity

- Can any source physical path, URI, rooted key, traversal key, or separator-
  mixed key reach a topic write or `ArtifactEntry.Unknown`?
- Does the build pass validate the complete import before side effects, rather
  than only validating the first entry or the index shape?
- Is the source artifact key treated as opaque and regenerated locally rather
  than trusted as a physical path?

### Test adequacy

- For each new assertion, identify one mutant that would make the test pass
  while the behavior is broken. In particular, test whether removing the
  production `PhysicalLocationKeys` entry, disabling ordinal-ignore-case
  matching, or restoring the old `Name` guard is observed.
- Confirm the test method's `MemberData` enumerates exactly the production key
  vocabulary and does not merely assert that a generic absence condition holds.
- Check whether the focused test command exercises both the new address-led
  path and the complete pre-write rejection guarantee.

## Review discipline

Trace each fix premise to the implementation and its callers. Do not infer
“pre-existing,” “unreachable,” “complete,” or “before any write” from comments;
establish those claims from the control flow, store calls, and executable tests.
