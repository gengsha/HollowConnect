@{
    # Paths are relative to docs/. Keep player delivery separate from developer references.
    Player = @(
        'README.md'
        'BOUNCYCASTLE-LICENSE.md'
        'SHARED-PROGRESS.md'
        'LINKED-HEALTH.md'
        'OBSERVATION.md'
        'SKIN-COMPATIBILITY.md'
        'KNOWN-ISSUES.md'
        'UPDATE-{version}.md'
        'frpc.example.toml'
    )
    Developer = @(
        'CHANGELOG.md'
        'DESIGN.md'
        'DEVELOPMENT.md'
        'SUPPORTED-ENTITIES.md'
        'TEST-INVENTORY-2026-10-18.md'
        'VERIFICATION.md'
        'technical/BOSS-SYNCHRONIZATION.md'
        # Legacy suite summarizers still consume these two versioned baseline ledgers.
        'evidence/runtime-blockers-0.1.13.json'
        'evidence/runtime-blockers-0.1.21.json'
    )
    # These records are consumed by release verification. Preserve their original hashes.
    Evidence = @(
        'evidence/README.md'
        'evidence/audit-remediation-0.1.7.md'
        'evidence/development-validation-{version}.json'
        'evidence/runtime-room-{version}.json'
        'evidence/runtime-blockers-{version}.json'
    )
}
