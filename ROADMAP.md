# Community and Pro roadmap

Packman Community is the public Apache 2.0 desktop application in this repository. It includes the existing PSADT packaging, script editing, remote testing, local certificate authentication/signing and Intune management workflows. No subscription is required for those capabilities.

## Community foundation

- Consistent, accessible Windows UI in dark and light themes.
- Package preflight, reliable signing failure handling and focused behavior validation.
- Native Windows screen previews and downloadable CI builds.
- Contribution guidance, private security reports and dependency notices.
- Public integration contracts that keep the community app independently buildable.

## Planned commercial integrations

These are development priorities, not features currently available in the community build. Release dates, pricing and commercial licensing will be announced separately.

| Priority | Pro integration | Intended outcome |
| --- | --- | --- |
| 1 | Key Vault authentication | Select a vault and use centrally managed credentials and certificates for Intune authentication and signing, with clear access and expiry feedback. |
| 2 | WDAC catalog generation | Capture the relevant installation/runtime files, generate and sign a catalog, validate its coverage and export deployment evidence for App Control for Business. |
| 3 | GitLab pipelines | Connect package source to a controlled build/test/publish pipeline with run status, artifacts and promotion of the exact tested package. |

The implementation sequence establishes identity and signing before automating WDAC and pipeline publishing.

Commercial implementations belong in a separate private codebase. They should compose the public services rather than fork the community UI or move existing community features behind a payment requirement. The public app will display capabilities it can actually execute.
