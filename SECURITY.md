<!-- SPDX-License-Identifier: MIT -->
# Security policy

## Reporting a vulnerability

Please use GitHub's **Report a vulnerability** flow in the repository Security tab. Do not open a
public issue for a suspected secret exposure, unsafe provider-call path, artifact-integrity bypass,
or prompt/evidence disclosure.

Include the affected commit, reproduction steps, impact, and the smallest safe evidence needed to
verify the issue. Do not include real credentials, customer data, raw provider responses, or a live
system prompt.

## Supported version

Security fixes target the current `main` branch. This portfolio sample is not a hosted service and
does not provide a production security SLA.

## Execution boundary

Credential-free deterministic execution is the default. Live plans require explicit paid opt-in.
Never commit `.env` files, credentials, certificates, local runtime receipts, or provider traces.
