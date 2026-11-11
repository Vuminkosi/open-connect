# Kiroween 2026 compliance

**Assessment date:** 2026-11-11
**Source:** [official Kiroween 2026 Devpost page](https://kiroween.devpost.com/)
**Repository revision:** `c179954`

## Eligibility

| Requirement | Status | Evidence or action |
|---|---|---|
| New project started during submission period | VERIFIED by repository history | First repository commit is 2026-11-06. Official period is 2026-10-30 through 2026-12-04. Preserve this history. |
| Working software application | PARTIAL | The repository contains working libraries, samples and tests, but the complete public demo path is not yet verified. |
| Public GitHub repository | NOT VERIFIED | Current origin/public visibility was not established by local evidence. Confirm before submission. |
| Open-source license visible | VERIFIED | Root `LICENSE` is MIT. GitHub About/license visibility remains owner action. |
| Root `/.kiro` present | VERIFIED | `.kiro/steering` and `.kiro/specs` exist in the repository. Do not add `.kiro` to ignore rules. |
| Category selection | RECOMMENDED: Skeleton Crew | OpenConnect is a reusable .NET-first framework. The submission must demonstrate two distinct applications built from it, not only a library test suite. |
| Working app URL | NOT VERIFIED | Provide a public deployed demo or documented local judge path. No deployment was performed. |
| Demo video under three minutes | NOT VERIFIED | Record a focused end-to-end flow on the built device and publish to public YouTube. |
| Kiro usage write-up | PARTIAL | Steering and specs exist. Add the final write-up with concrete Kiro IDE/spec/steering/hook/MCP decisions and outcomes. |
| No unauthorized copyrighted material | NOT VERIFIED | Review demo music, screenshots, model assets, logos and third-party code before publication. |

## Recommended project framing

**Category:** Skeleton Crew.
**Project:** OpenConnect, a .NET-first application harness around native Microsoft Agent Framework capabilities, demonstrated through two separate applications: a Blazor task/privacy console and a worker-backed privacy-protected agent sample. The framework supplies reusable composition, authorization, privacy and durable application boundaries while MAF remains the execution engine.

This framing is eligible only if the final repository visibly contains two separate application folders and the submission demonstrates both. Existing `samples/Blazor` and `samples/workers` are candidates; the actual working URLs and journeys still require verification.

## Required submission package

1. Public GitHub URL with all source, assets, instructions, MIT license and root `.kiro`.
2. Working app URL(s), or judge instructions for a reproducible local run without private credentials.
3. Plain-language feature description and audience/problem statement.
4. Public YouTube demo under three minutes showing the app working.
5. Kiro development write-up covering IDE/CLI/Crew usage if applicable, specs, steering, hooks, MCP, powers and testing strategy.
6. Optional bonus links only when their separate conditions are actually met.

## Demo acceptance script

- Start from a clean checkout with documented commands.
- Show the Blazor application submitting a task through the authenticated OpenConnect gateway.
- Show native MAF execution and a real status/result transition.
- Show a synthetic sensitive value being detected and masked or blocked by the host privacy boundary.
- Show the worker unavailable/unauthorized negative path and the application remaining fail-closed.
- Show the second application using the same framework contracts.
- Avoid raw credentials, personal data, private model tokens, copyrighted music and claims beyond recorded evidence.

## Concerns before final project choice

- The repository is eligible by commit history, but eligibility is not the same as submission readiness.
- The `Skeleton Crew` category requires two separate application folders and two distinct applications. This must be made explicit in the demo and repository layout.
- The current local environment contains credential-shaped ignored values. Do not record them, ship them, or use them for judging. Rotate any real credentials.
- A public deployment, public source URL, and under-three-minute video are owner actions and are not established by this audit.
