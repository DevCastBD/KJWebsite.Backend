<!-- phase: WRAP-UP | branch: Feature/issue-216-privacy-test-suite | tasks: 9/10
     base: 303e05e | updated: 2026-11-16
     next: independent reviewer on the branch diff, fix findings, then ask the user before push and PR
     -->
# Plan: Privacy test suite (BE-109, #216). Full design: C:\Users\Tahmid\.claude\plans\merry-chasing-thimble.md
## Tasks
- [x] A: PrivacyStack harness (gateway plus six services in memory, wired), GlobalLogSink on all factories, TestTokens.Expired, handler hooks
- [x] A: small source fixes: no-store middleware (Finance donors and donations, Identity donors) and Identity dev email sender log redaction, with tests
- [x] Donor CSV export fix for donors with no gifts, with a regression test (user asked; found by task A)
- [x] B: T-01, T-02, T-07 endpoint enumeration across all six services
- [x] C: end-to-end T-03, T-04, T-05, T-06, T-11, T-24, T-25, T-26 through the gateway with the real Finance
- [x] C: T-14 (Finance disable path), T-22 (twice through the gateway), T-27, T-28, T-29
- [x] D: T-21 public scan from openapi.v1.yaml and the final log scan (T-23)
- [x] Docs: docs/PRIVACY_TESTS.md, MASTER_PLAN row for BE-114
- [x] CORS X-Correlation-ID gateway fix (found by the live Playwright run; done, commit 4102f3d)
- [ ] Full suite three times (flake check), mutation checks, format, review, PR
## Tests
- High blast radius: the suite is the safety net. Each area gets a mutation check (break the guarantee, see the test fail naming the endpoint and the value, revert).
## Files
- tests/IntegrationTests/KJWebsite.IntegrationTests/** (PrivacyStack.cs, GlobalLogSink.cs, Privacy*Tests.cs, TestInfrastructure.cs, csproj), Directory.Packages.props
- src: Finance and Identity header middleware, AuthEmail.cs; docs/PRIVACY_TESTS.md, docs/MASTER_PLAN.md
