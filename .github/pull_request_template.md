<!--
  Thanks for contributing to LetsSSL4Windows! Fill in the sections below and
  delete this comment. Keep the description focused on what changed and why —
  the checklist at the bottom is a reminder, not a gate for every box.
-->

## Summary

<!-- What does this PR do, and why? One or two sentences is fine. -->

## Type of change

<!-- Delete the ones that don't apply. -->

- Bug fix
- New feature
- Refactor / cleanup (no behavior change)
- Documentation
- Build / CI / tooling

## Changes

<!--
  Group by area so reviewers can jump to what they care about. Delete areas
  you didn't touch. Both editions ship in every release, so call out when a
  change lands in only one of them (and why).
-->

- **Core (`LetsSSL.Core`):**
- **Desktop app (`LetsSSL.App`, WPF):**
- **PowerShell edition (`powershell/`):**
- **Docs:**

## Testing

<!--
  How was this verified? Note new/updated unit tests (xUnit) and Pester tests,
  plus anything checked by hand — especially Windows-only behavior (cert store,
  IIS, WinRM, scheduled task/service) that CI can't exercise.
-->

## Checklist

- [ ] Desktop and PowerShell editions kept at **feature parity** (or the gap is explained above)
- [ ] Added or updated **tests** (`tests/LetsSSL.Core.Tests` and/or `powershell/tests`)
- [ ] Updated **docs** (`README.md` and/or `powershell/README.md`) if behavior or usage changed
- [ ] No secrets, tokens, or credentials committed; stored secrets still use DPAPI
- [ ] CI is green (build, .NET tests, and Pester)

## Related issues

<!-- e.g. "Closes #123" — or delete this section. -->
