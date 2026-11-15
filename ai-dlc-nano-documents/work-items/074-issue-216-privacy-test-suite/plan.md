<!-- phase: CONSTRUCT | branch: Feature/issue-216-privacy-test-suite | tasks: 0/8
     base: 303e05e | updated: 2026-11-16
     next: task A via Codex: PrivacyStack harness, GlobalLogSink, TestTokens.Expired, factory hooks, the two small source fixes, with their tests -->
# Plan: Privacy test suite (BE-109, #216). Full design: C:\Users\Tahmid\.claude\plans\merry-chasing-thimble.md
## Tasks
- [ ] A: PrivacyStack harness (gateway plus six services in memory, wired), GlobalLogSink on all factories, TestTokens.Expired, handler hooks
- [ ] A: small source fixes: no-store middleware (Finance donors and donations, Identity donors) and Identity dev email sender log redaction, with tests
- [ ] Donor CSV export fix for donors with no gifts, with a regression test (user asked; found by task A)
- [ ] B: T-01, T-02, T-07 endpoint enumeration across all six services
- [ ] C: end-to-end T-03, T-04, T-05, T-06, T-11, T-24, T-25, T-26 through the gateway with the real Finance
- [ ] C: T-14 (Finance disable path), T-22 (twice through the gateway), T-27, T-28, T-29
- [ ] D: T-21 public scan from openapi.v1.yaml and the final log scan (T-23)
- [ ] Docs: docs/PRIVACY_TESTS.md, MASTER_PLAN row for BE-114
- [ ] Full suite three times (flake check), mutation checks, format, review, PR
## Tests
- High blast radius: the suite is the safety net. Each area gets a mutation check (break the guarantee, see the test fail naming the endpoint and the value, revert).
## Files
- tests/IntegrationTests/KJWebsite.IntegrationTests/** (PrivacyStack.cs, GlobalLogSink.cs, Privacy*Tests.cs, TestInfrastructure.cs, csproj), Directory.Packages.props
- src: Finance and Identity header middleware, AuthEmail.cs; docs/PRIVACY_TESTS.md, docs/MASTER_PLAN.md
