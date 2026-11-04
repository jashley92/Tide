# 0002. Roles are fixed bundles of permission atoms; AD groups grant roles

- **Status:** Accepted
- **Recorded:** 2026-10-25

## Context

Access comes from Active Directory, and every organization names its groups differently. The app needs a small,
predictable access model that an open-source user can map onto their own groups.

## Decision

- There are four built-in roles: Analyst, Action owner, Reader and Admin. Each is a fixed set of `Permission` atoms
  defined in `RoleDefinitions`.
- Authorization policies require a permission claim, never a role.
- Administrators map AD groups to roles in the app. On first run the mappings are seeded from the `RoleMapping`
  configuration.

## Consequences

- Adding a capability means adding a permission and granting it to roles in code, with a test.
- Custom roles aren't supported. If they're needed later, they compose the same atoms.
- The last mapping that grants Admin can't be removed, so the app can't lock itself out.
