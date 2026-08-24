# Blue Shell Speech

A practice-management platform and public website for a solo Maryland pediatric
speech-language pathologist.

Two audiences in one product: parents looking for a speech therapist for their child, and the
clinician documenting sessions afterward. The boundary between them is strict — the public site
never touches clinical data, and the clinical application is never reachable without
authentication.

**Status:** in development. Synthetic data only. Not yet in production use.

---

## The problem

An SLP doing in-home early-intervention visits finishes a session in a family's living room and
has to write a SOAP note. In practice that happens hours later, at the kitchen table, from
memory — which is slow, and which loses detail that mattered.

The hero feature is a post-session voice recap. She speaks naturally for two to five minutes;
the system produces a structured draft she reviews and signs.

---

## Architecture

```
Browser / PWA  ──HTTPS──►  web (Next.js)  ──internal──►  api (ASP.NET Core)
                            public ingress                 no public ingress
                                                                  │
                                    ┌─────────────┬───────────────┼──────────────┐
                                    ▼             ▼               ▼              ▼
                               Azure SQL    Blob Storage    Azure Speech   Azure OpenAI
                              system of      audio, 30d        STT          text only
                                record        max                          de-identified
```

| Layer | Choice |
|---|---|
| Frontend | Next.js 16 (App Router), React 19, TypeScript strict |
| Backend | ASP.NET Core (.NET 10) — system of record |
| Data | EF Core → Azure SQL Database |
| Auth | ASP.NET Core Identity, self-hosted, mandatory TOTP MFA |
| Transcription | Azure Speech |
| Extraction + generation | Azure OpenAI, text endpoints only |
| Hosting | Two containers on Azure Container Apps, scale-to-zero |

**The browser never calls the .NET API.** All traffic terminates at Next.js route handlers,
which call the API server-to-server. The API has internal ingress — not an unlinked endpoint, but
no public route at all. Access tokens stay in server memory; the browser holds an `HttpOnly`
session cookie and nothing else.

---

## Three decisions worth the click

**Generation never sees the transcript.** The pipeline is
`audio → STT → de-identify → extract → validate → generate`, and the generation step receives
only validated structured data. A model that has not read the source text cannot quote a number
from it. A post-generation check re-extracts every numeric from the output and requires each to
trace to validated input — no provenance, no draft. Clinical notes are full of numbers, so a
fluent model asked to write one *will* supply them; prompting against that is a request, while
withholding the material is a guarantee.

**Signed notes are immutable, and the database enforces it.** Amendments insert a new version
with a required reason and a self-referencing link to the superseded row. There is an `UPDATE`
trigger rejecting edits to signed content, a filtered unique index guaranteeing one current note
per appointment, and a SHA-256 content hash computed at signature. Application-layer
immutability holds right up until someone opens SSMS at 11pm.

**Every extracted quantitative field is nullable, with no default.** Trials, accuracy, cue
level — all of them. A non-nullable `trialsAttempted` defaulting to `0` is how a model's silence
becomes a clinical claim of zero trials. Null means *not stated*, and drives a chip the clinician
fills in by typing or by speaking.

---

## Repository

```
web/     Next.js — public site, clinical UI, BFF route handlers
api/     ASP.NET Core — Domain / Application / Infrastructure / Api
docs/    Architecture, data model, threat model, AI pipeline, slice plan
infra/   Idempotent provisioning scripts
```

`api/src/Practice.Domain` references nothing — not EF Core, not ASP.NET. Clinical invariants are
testable in milliseconds with no infrastructure, and an architecture test fails the build if that
stops being true.

Start with [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), then
[`docs/AI_PIPELINE.md`](docs/AI_PIPELINE.md).

---

## Running locally

```bash
docker compose up          # SQL Server, Azurite, api, web
```

```bash
cd web && npm ci && npm test && npm run typecheck && npm run lint
cd api && dotnet test
```

Requires .NET 10 SDK, Node 22, Docker.

Local development points at **real** Azure Speech and OpenAI endpoints with synthetic data.
Mocking a model provider teaches nothing about latency, refusals, or malformed output — which
are exactly the failure modes the app has to survive.

---

## On compliance

This system is **designed to support HIPAA obligations**. It is not described as "HIPAA
compliant" anywhere, because compliance is a property of an organization's practices, not of
software.

Controls implemented from the first commit: managed identity throughout with no database
password in existence, audit logging that records reads as well as writes, no PHI in logs or
browser storage, de-identification before any model call, and separate development and
production Azure subscriptions so the environment used for development is structurally incapable
of holding patient data.

Work that must complete before any real patient record exists is tracked openly in
[`docs/PRELAUNCH_BLOCKERS.md`](docs/PRELAUNCH_BLOCKERS.md) — including the items that are still
unresolved, each with a named fallback. Development uses synthetic data exclusively until every
one of them closes.

The governing question, from the spec:

> Would we be comfortable putting an actual child's medical information through this exact data
> path?

---

## License

Not currently licensed for reuse. Published for review.
