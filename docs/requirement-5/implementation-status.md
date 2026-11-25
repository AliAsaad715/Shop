# Requirement 5 implementation audit

Checked 25 November 2026 against all fetched remote branches, main, and GitHub PR state before starting this work.
Tasks already implemented in another branch were excluded from this change.

| Task | Verified state before this change | Action |
| --- | --- | --- |
| R5-01 | Local-state audit merged to main in [#53](https://github.com/AliAsaad715/Shop/pull/53) | Reuse |
| R5-02 | Nginx least_conn/zone/resolve/health/WebSocket/log config merged in [#54](https://github.com/AliAsaad715/Shop/pull/54) | Reuse |
| R5-03 | Three APIs/workers, X-Instance and Nginx target merged in [#56](https://github.com/AliAsaad715/Shop/pull/56) | Reuse |
| R5-04 | Explicit migrator merged in [#57](https://github.com/AliAsaad715/Shop/pull/57) | Reuse |
| R5-05 | Trusted forwarding merged in [#58](https://github.com/AliAsaad715/Shop/pull/58) | Reuse |
| R5-06 | Redis limiter implemented in open [#60](https://github.com/AliAsaad715/Shop/pull/60), branch `feature/r5-06-redis-login-rate-limit` | No duplication |
| R5-07 | S3/SeaweedFS/import/proof implemented in [#59](https://github.com/AliAsaad715/Shop/pull/59), branch `feature/r5-07-object-storage` | No duplication; #59's base was `feature/r5-05-forwarded-headers`, so main still lacks it |
| R5-08 | Product details still used a five-second L1; no implementation on another remote branch | Disable details L1, keep Redis, add three-host body/ETag regression |
| R5-09 | Primary/read/worker strings had no pool caps, with Postgres default 100 | Budget both write and failover-read pools; check both servers |
| R5-10 | WebSockets-only/skip negotiation/reconnect merged in [#55](https://github.com/AliAsaad715/Shop/pull/55) | Reuse |
| R5-11 | Three workers already existed, but production email prefetch was five | Prefetch one; real-broker fairness/loss/replay tests |
| R5-12 | Partial Nginx/Compose/audit scripts already existed; no combined kit or least_conn ADR | Reuse existing proofs; add automated before/after, k6 continuity/comparison, budget checks and ADR |

This branch starts from main and adds only R5-08/R5-09/R5-11/R5-12. It does not merge or reimplement the other
branches. The [proof kit](../../tests/proof/requirement-5/README.md) states prerequisites, what is measured and
where evidence is saved. Audit finding 9 (unclaimed pending warehouse releases) remains separately documented;
it is not one of the plan's twelve tasks and must not be presented as already fixed.
