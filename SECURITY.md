# Security Policy

FirstBlick is under active development. Automated CI and normative test fixtures do not constitute an independent security audit, certification, or production approval.

## Reporting a vulnerability

**Do not post exploitable details, credentials, personal data, real financial records, customer documents, or proof-of-concept payloads in public issues, pull requests, comments, or CI logs.**

If GitHub private vulnerability reporting is enabled for this repository, use its private reporting interface. Otherwise, contact the maintainer through an established **private** communication channel. Do not create a public issue containing the vulnerability details. If no private channel is available, retain the report privately until a suitable disclosure route has been agreed.

For triage, include the affected commit/version, a concise impact assessment, a minimal reproduction using synthetic data, and redacted logs. Never include live secrets or third-party data.

## Security-sensitive areas

Reports involving authentication and authorization, tenant isolation, file uploads and document parsing, invoice/payment data, deployment secrets, unsafe dependencies, or externally supplied building-physics inputs are particularly important. Independent review and representative deployment testing remain necessary before production use.

Security fixes should use focused regression tests, least-privilege access and reviewed pull requests. No response-time guarantee or security certification is implied by this policy.
