# Security Policy

## Supported Versions

As this project is under active development, we currently support the latest release.

| Version | Supported          |
| ------- | ------------------ |
| 1.0.0   | :white_check_mark: |
| < 1.0   | :x:                |

The v1.2 changes are under development and have not been released. Until release, use only a reviewed build and keep its loopback endpoint private.

## v1.2 endpoint controls

- Bind only to `127.0.0.1`; validate Host and Origin and authenticate each HTTP request.
- The protected endpoint descriptor retains the owner bearer for owner routes and the legacy trusted path. The connection-key bearer cannot call owner issue/cancel/revoke/status or shutdown endpoints.
- The stateful `/mcp` transport admits a one-use connection key to one MCP session. Every request requires both the grant's bearer value and its bound `Mcp-Session-Id`. A session ID, token, OTP, or owner bearer alone cannot select a keyed session.
- Status APIs return counts and expiry metadata only. Credentials, request headers, session IDs, and raw SDK error messages must not be logged.
- Build tools require `allowBuild=true`, an absolute matching solution path, explicit target PID/start-time binding, design mode, and no active build. Project registration requires `allowProjectMutation=true`, matching solution/project identity, design mode, no build, and clean project/source state.
- Project inventory is limited to IDE-registered projects and items. It does not enumerate arbitrary disk paths. Snapshot count, byte size, TTL, project/item traversal, and serialized response size are capped. Removing an item unregisters it from the project and verifies that the physical file remains.
- If a COM operation times out after dispatch, preserve the workspace gate until its queued completion/cleanup settles and report an uncertain outcome. Do not retry a mutation blindly.

## Reporting a Vulnerability

We take the security of this MCP server seriously, especially given its access to local Visual Studio processes and COM automation interfaces.

If you discover a security vulnerability, please **DO NOT** open a public issue. Publicly disclosing vulnerabilities can put users at risk before a patch is available.

Instead, please report it via the following method:

**Email:** Send details to **alon@miniforge.studio**  
**Private Advisory:** use the Private Advisory feature to submit a report directly to the us.

### Information to Include: 
* Description of the vulnerability (e.g., improper COM object handling, unauthenticated command execution).
* Steps to reproduce the issue.
* Any relevant trace logs or call stacks.

We aim to acknowledge reports within 48 hours and will provide a timeline for a fix.


