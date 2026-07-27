# Local media creation

## Purpose

GigaClaw can coordinate governed image and video work on local hardware without turning an agent
run into a long-lived render process. Agents make and review creative decisions; a durable job
service executes immutable specs through the user's OpenMontage, ComfyUI, and Phosphene runtimes.

OpenMontage is the production authority for video. ComfyUI and Phosphene are execution providers,
not alternative orchestration paths. A generated clip remains an asset candidate until the
OpenMontage pipeline advances it through its checkpoints.

## Team

The **Local Media Creation** board filter contains:

- `local-media-director` — locks direction, provider, workflow, provenance, and approval.
- `local-image-artist` — submits approved ComfyUI image candidates.
- `local-motion-artist` — submits approved ComfyUI or Phosphene clip assets.
- `local-media-compositor` — executes OpenMontage edit and composition stages.
- `local-media-reviewer` — independently validates the artifact, receipt, and visual quality.
- Shared producer, approval, monitoring, commit, evaluation, and documentation agents.

The producer remains the sole owner of ticket decomposition. Reviewers do not take ownership of
production tickets, and only the human owner moves accepted work to `Done`.

## Durable execution flow

1. The director reads OpenMontage's `AGENT_GUIDE.md`, selects a declared provider, reads the
   provider's Layer 3 skills, and writes an immutable version-1 JSON execution spec.
2. The human approves the exact provider/model/workflow decision. The spec records the approver,
   licensing notes, and Layer 3 skills read.
3. An image or motion agent submits the workspace-relative spec to
   `POST /api/projects/{slug}/media/jobs` with an idempotency key and the submitting agent's
   `author`.
4. `LocalMediaJobService` validates and stores the job in the project SQLite database, moves the
   ticket to `Backlog`, and returns immediately.
5. The hosted job pump serializes jobs by resource class (`gpu:comfyui` or `mlx:phosphene`), applies
   provider-specific timeouts, executes `.agents/scripts/media_generate.py`, and writes an atomic
   receipt.
6. Success moves the ticket to `Review`; failure or restart interruption moves it to `Blocked`.
   The `local-media-reviewer-on-review` automation checks provenance, bytes, ffprobe data, sampled
   frames, and visual quality.
7. Moving the ticket from `Review` to `Done` approves an awaiting job. Moving it back to `Todo`
   rejects that candidate and preserves the producing assignment for correction.

Queued/running jobs may be cancelled through the API. A host restart marks an in-flight job
`interrupted` instead of pretending it completed or silently retrying expensive work.

## Execution-spec contract

Specs live under `media/specs/`; paths supplied to the API must be workspace-relative.

```json
{
  "version": 1,
  "kind": "image",
  "provider": "comfyui",
  "prompt": "Approved provider-specific prompt",
  "outputPath": "media/renders/42/candidate.png",
  "parameters": {
    "width": 1024,
    "height": 1024,
    "steps": 4,
    "seed": 42
  },
  "runtime": {
    "comfyuiServerUrl": "http://127.0.0.1:8188"
  },
  "governance": {
    "approvalStatus": "approved",
    "approvedBy": "owner",
    "licenseNotes": "Model and source license notes",
    "layer3SkillsRead": ["comfyui", "flux-best-practices"]
  }
}
```

`provider: auto` is rejected. Clip specs additionally require an `openMontage` object containing an
absolute checkout path, project ID, pipeline, `stage: "assets"`, and a checkpoint path. The checkpoint
must exist inside the locked OpenMontage project and record matching `project_id`, `pipeline_type`,
`status: "completed"`, and `human_approved: true`.

Outputs may be written only inside the GigaClaw workspace or the locked OpenMontage project.
`media/renders/` is ignored by the project template; specs, receipts, and reviews remain suitable for
version control.

## API

- `GET /api/projects/{slug}/media/jobs`
- `GET /api/projects/{slug}/media/jobs/{id}`
- `POST /api/projects/{slug}/media/jobs`
- `POST /api/projects/{slug}/media/jobs/{id}/cancel`
- `POST /api/projects/{slug}/media/jobs/{id}/review`

Creation returns `202 Accepted`; replaying the same idempotency key returns the existing job with
`200 OK`. Every mutation requires `author`. Cancel accepts `{ "author": "..." }`; review accepts
`{ "decision": "approved|rejected", "author": "..." }`.

## Runtime configuration

- `OPENMONTAGE_PATH` — fallback OpenMontage checkout for image jobs.
- `OPENMONTAGE_PYTHON` — optional Python executable override; otherwise the spec runtime, the
  OpenMontage virtual environment, or system Python 3 is used.
- `COMFYUI_SERVER_URL` — canonical ComfyUI API URL.
- `PHOSPHENE_SERVER_URL` — canonical Phosphene API URL; local ports 8198 and 8199 are probed as the
  same provider, not treated as a provider substitution.
- `GIGACLAW_MAX_CONCURRENT_MEDIA_JOBS` — global job limit, default `2`. Each provider resource class
  remains serialized independently.

Python 3.10+ is required by the worker. OpenMontage, its selected provider dependencies, local
models, ffmpeg, and ffprobe are external dependencies; GigaClaw does not install or download them.
The system watchdog performs only read-only probes and reports missing configuration as `unknown`.

## Key components

- `GigaClaw.Core/Services/LocalMediaJobService.cs` — persistence, scheduling, locks, timeout,
  cancellation, restart recovery, and board reconciliation.
- `GigaClaw.Web/Api/Endpoints.Media.cs` — job API.
- `ProjectTemplate/Agents/scripts/media_generate.py` — deterministic, policy-guarded provider call.
- `ProjectTemplate/Agents/scripts/media_contract.py` — receipt, artifact, ffprobe, and frame checks.
- `ProjectTemplate/Agents/local-media-*/` — role skills and memory.
- `ProjectTemplate/Agents/automations.json` and `contracts.json` — dispatch and write boundaries.

## External dependencies

- [Automation engine](./automation-engine.md) — dispatches artists and the independent reviewer.
- [Storage](./storage.md) — project-local SQLite job records.
- [REST API](./rest-api.md) — submission, cancellation, status, and review endpoints.
