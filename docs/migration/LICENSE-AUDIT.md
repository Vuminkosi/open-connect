# OpenConnect License Audit

**Date:** 2026-11-06
**Status:** Initial audit for Kiroween 2026

## Project License

**OpenConnect License:** MIT License (to be confirmed)

## Source Reference License

**Swift-Aid License:** MIT License
**Reference Commit:** 5a6bad62f87d9e9dfabbc2d0fa1a0b491f0bada7

### Extraction Rules

1. Swift-Aid MIT-licensed code may be adapted with attribution
2. Clinical/domain-specific code must be rewritten, not copied
3. All adaptations must be documented in swiftaid-inventory.csv

## Third-Party Dependencies

### .NET Dependencies (To Be Verified)

| Package | License | Status | Notes |
|---|---|---|---|
| Microsoft.AgentFramework | MIT | Pending | Verify actual package name and license |
| MudBlazor | MIT | Pending | UI components |
| Npgsql | PostgreSQL | Pending | PostgreSQL driver |
| NATS.Net | Apache-2.0 | Pending | NATS client |
| Microsoft.EntityFrameworkCore | MIT | Pending | ORM |

### Python Dependencies (To Be Verified)

| Package | License | Status | Notes |
|---|---|---|---|
| pydantic | MIT | Pending | Data validation |
| nats-py | Apache-2.0 | Pending | NATS Python client |
| fastapi | MIT | Pending | Worker HTTP framework |
| uv | MIT/Apache-2.0 | Pending | Package manager |

### Model Weights and Checkpoints

| Model | License | Status | Notes |
|---|---|---|---|
| MMS-TTS | CC-BY-NC | Pending | Verify redistribution rights |
| NLLB | CC-BY-NC | Pending | Verify redistribution rights |
| GLiNER | Apache-2.0 | Pending | Optional PII detector |
| DeBERTa | MIT | Pending | Optional injection detector |
| Qwen3Guard | To Verify | Pending | Optional safety classifier |
| PaddleOCR | Apache-2.0 | Pending | Optional OCR |
| YuNet | Apache-2.0 | Pending | Optional face detection |
| Silero VAD | MIT | Pending | Voice activity detection |

### Model Redistribution Policy

- Model weights must NOT be bundled in NuGet/npm packages
- Model weights must NOT be included in default Docker images
- Operators must explicitly download/provision model artifacts
- License notices must be included for all incorporated models

## Audit Checklist

- [ ] Verify all NuGet dependency licenses
- [ ] Verify all npm dependency licenses
- [ ] Verify all Python dependency licenses
- [ ] Confirm model weight licenses and redistribution rights
- [ ] Document any GPL/AGPL dependencies (require legal review)
- [ ] Create THIRD-PARTY-NOTICES file
- [ ] Add license headers to all source files

## Prohibited Content

The following must NEVER be redistributed:

- Unlicensed model weights
- Proprietary code without permission
- Trade secrets or confidential information
- Patient data or personally identifiable information
- Production credentials or secrets

## License Verification Commands

```bash
# .NET
dotnet list package --include-transitive --format json | jq '.projects[].packages[] | {name: .id, version: .version, license: .license}'

# Python
pip-licenses --format=json

# npm
npm list --json | jq '.dependencies | to_entries[] | {name: .key, license: .value.license}'
```

## Third-Party Notices Template

```
This application makes use of the following third-party packages:

[Package Name] Copyright (c) [Year] [Author]
Licensed under the [License Name]
[License URL]
```

---

**Audit Status:** In Progress
**Next Review:** Before first release
**Responsible:** OpenConnect Team
