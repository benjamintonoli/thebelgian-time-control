# Locked holdout final evaluation

- EvaluatedAt: 2026-07-30T14:58:41.2122604+00:00
- GitCommit: `b63512838c08def53a3ce4f607a16f5ff647cbf9`
- GitTag: `(none)`
- ConfigurationHashSha256: `b4cccfa21f20e5d3be59b992fcdb8352849c36dd1d24529990235e918565b043`
- HoldoutManifestHashSha256: `206a0ac89162151a6b236deb3047c9574d84409c2b3a82b2ff2f6c415d08f2b9`
- HoldoutContentSha256: `206a0ac89162151a6b236deb3047c9574d84409c2b3a82b2ff2f6c415d08f2b9`
- Decision: **NO-GO**

## Metrics

| Metric | Value |
|---|---:|
| Cases | 59 |
| Accepted | 3 |
| Correct accepted | 2 |
| Precision | 0.6667 |
| Coverage | 0.0508 |
| False positives | 1 |
| False negatives | 1 |
| Wrong VisitCandidate | 0 |
| Abstentions | 56 |

## Label distribution

- Ambiguous: 3
- CorrectCandidate: 3
- NoValidCandidate: 53

## Error categories

- FN_CorrectCandidate: 1
- FP_NoValidCandidate: 1

## Decision notes

- Offline-only holdoutevaluatie; geen Plenion/Powerfleet/Geoapify-toegang.
- Coverage is informatief en geen zelfstandig afkeurcriterium.
- Holdout: 59 cases, 2025-10-01 t/m 2025-12-31, één technieker.

## Errors

- 264201|FN_CorrectCandidate|CorrectCandidate|Unresolved|status=AddressDataIssue;sources=0;recovery=False
- 265057|FP_NoValidCandidate|NoValidCandidate|Probable|status=ConfirmedLocationMatch;sources=1;recovery=False
