# The public keys TIDE's release bundles are signed with. Upgrade-Tide.ps1 and Test-TideBundle.ps1 trust a bundle
# whose tide-<v>.zip.sig verifies against one of these (RSA-PSS, SHA-256), and, while any is pinned, refuse one with
# no signature unless -AllowUnsigned is passed. With none pinned they check the SHA-256 only, as before signing.
#
# Each entry: @{ Id = '<first 16 hex characters of the SHA-256 of the key's SubjectPublicKeyInfo>'; Added = '<date>';
#                Xml = '<RSAKeyValue><Modulus>...</Modulus><Exponent>...</Exponent></RSAKeyValue>' }
#
# Don't edit by hand: tools/release/New-ReleaseSigningKey.ps1 adds a key above the marker line below. It's an array so a
# new key can be added, and shipped in a release signed by the old one, before releases are signed with the new one
# (docs/development/releasing.md, "Rotating the key"). Removing a key stops installs that upgrade past this file from
# trusting it.
#
# Dot-source this file. ASCII only.
$TideReleaseKeys = @(
    # --- New-ReleaseSigningKey.ps1 adds each key above this line ---
)
