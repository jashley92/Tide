# The public keys TIDE's release bundles are signed with. Upgrade-Tide.ps1 and Test-TideBundle.ps1 trust a bundle
# whose tide-<v>.zip.sig verifies against one of these (RSA-PSS, SHA-256), and, while any is pinned, refuse one with
# no signature unless -AllowUnsigned is passed. With none pinned they check the SHA-256 only, as before signing.
#
# Each entry: @{ Id = '<first 16 hex characters of the SHA-256 of the key's SubjectPublicKeyInfo>'; Added = '<date>';
#                Xml = '<RSAKeyValue><Modulus>...</Modulus><Exponent>...</Exponent></RSAKeyValue>' }
#
# Don't edit by hand: tools/signing/New-ReleaseSigningKey.ps1 adds a key above the marker line below. It's an array so a
# new key can be added, and shipped in a release signed by the old one, before releases are signed with the new one
# (docs/development/releasing.md, "Rotating the key"). Removing a key stops installs that upgrade past this file from
# trusting it.
#
# Dot-source this file. ASCII only.
$TideReleaseKeys = @(
    @{ Id = 'f30ac4d151c58d32'; Added = '2026-11-07'; Xml = '<RSAKeyValue><Modulus>scjw4FaRE5IDDoLvUz1qp9cQ3H+Nu49HyCtVqWzt53KX7XfemLkOsteF4Agwns93VBXGYbkMNj9lDctiIVcQSYNZ/MBZHfnCTENG/SbawH5NTF1rpr3vbKHXY5cyzWIssf/RbVROEkhL3tTzcz8ui61o8qH9zZMgvkyjKVmYRhighLZ4CBxa537yTnWbC7wEQ5TYCrs7wfjgJjCXG3kFExASzBQ8+nVQGTj91hNkcunB0KqoeSxJYYuXgOKiTwO71XMTOS2ADfvuhtXGjc02qfVwy5Ww6hQPLFizuDU92C2Aj03UMRB+AwVTnRlEzh8FW1woB5mmmKjGanOMi+U/vH+RVsp7cN5gcFgytuOVkzaA4py+NW1Lz20PszyVuMhoXoVIC9g9Gy7MgPPflDOEBsImUrMS0yOCXuYnP21BKng+n2m/sXTLPObBidJJCIrEfZ4wXAyh1YpUD38fDDgZIWc3WdJWIXBw/asFA78VmzutXDn2CxGeaqt3EszZd47Z</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>' }
    # --- New-ReleaseSigningKey.ps1 adds each key above this line ---
)
