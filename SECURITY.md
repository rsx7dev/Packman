# Security reports

Please use [GitHub private vulnerability reporting](https://github.com/rsx7dev/Packman/security/advisories/new) for issues that could expose credentials, execute unintended commands, or change deployments outside the user's request. Avoid including secrets in public issues.

Include the Packman version or commit, Windows version, affected workflow, expected impact and the smallest reproduction you can provide. Use sample names and redact tokens, tenant details, private keys and customer data. A reproduction does not need access to a production tenant.

Development currently targets the latest code on `main`. A separate support period or response-time guarantee has not been established for older releases.

Packman runs with the permissions of the signed-in Windows user and the configured Microsoft identity. Keep the packaging share and PSADT templates under appropriate access control. Remote installation should be tested on a disposable device before a pilot deployment. Dependencies and PSADT runtimes keep their upstream support and security requirements.
